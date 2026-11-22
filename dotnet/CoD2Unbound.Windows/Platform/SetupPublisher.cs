using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using CoD2Unbound.Core;
using Microsoft.Win32.SafeHandles;

namespace CoD2Unbound.Windows.Platform;

internal static class SetupPublisher
{
    private static readonly Guid Authenticode = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    private const string CodeSigning = "1.3.6.1.5.5.7.3.3";
    private const uint RevocationUnavailable = 0x40 | 0x01000000;
    private static readonly PublisherRule[] Rules = ReadRules();

    internal static bool Configured => Rules.Length > 0;

    private static PublisherRule[] ReadRules()
    {
        string? configured = typeof(SetupPublisher).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "UpdatePublisherRules")?.Value;
        if (string.IsNullOrEmpty(configured))
            return [];

        var rules = new List<PublisherRule>();
        foreach (string entry in configured.Split('|', StringSplitOptions.TrimEntries))
        {
            // Accept profile OIDs in the private enterprise or UUID namespace. Generic
            // usages (including Code Signing itself and Any EKU) cannot be publisher rules.
            if (entry.Split('=', StringSplitOptions.TrimEntries) is not [var root, var oid] ||
                root.Length != 64 || !root.All(char.IsAsciiHexDigit) ||
                !Regex.IsMatch(oid, @"\A(?:1\.3\.6\.1\.4\.1|2\.25)(?:\.(?:0|[1-9][0-9]*)){2,}\z"))
                return [];
            rules.Add(new(root, oid));
        }
        return [.. rules];
    }

    internal static void Verify(string path, SafeFileHandle handle)
    {
        nint pathPointer = Marshal.StringToHGlobalUni(path);
        nint filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = pathPointer, Handle = handle.DangerousGetHandle() };
        Marshal.StructureToPtr(file, filePointer, fDeleteOld: false);
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(),
            Ui = 2, // WTD_UI_NONE
            Choice = 1, // WTD_CHOICE_FILE
            File = filePointer,
            Action = 1, // WTD_STATEACTION_VERIFY
            Flags = 0x80 | 0x2000 | (WindowsReleaseSource.LocalOrigin is null ? 0u : 0x1000u),
            Context = 1 // WTD_UICONTEXT_INSTALL
        };
        try
        {
            int status = WinVerifyTrust(-1, Authenticode, ref data);
            if (status != 0)
                throw new LauncherUpdateException("Windows could not verify the Setup signature.",
                    new CryptographicException(status));

            VerifyPublisher(data.State, WindowsReleaseSource.LocalOrigin is not null);
        }
        finally
        {
            data.Action = 2; // WTD_STATEACTION_CLOSE
            _ = WinVerifyTrust(-1, Authenticode, ref data);
            Marshal.FreeHGlobal(filePointer);
            Marshal.FreeHGlobal(pathPointer);
            GC.KeepAlive(handle);
        }
    }

    private static void VerifyPublisher(nint state, bool offlineFixture)
    {
        nint provider = state == 0 ? 0 : WTHelperProvDataFromStateData(state);
        nint signerPointer = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if (signerPointer == 0)
            throw new LauncherUpdateException("Windows did not return Setup's accepted signer.");

        // WinVerifyTrust selected the primary signature. Use its chain while the trust
        // state is open, without rebuilding it at today's date or examining nested signatures.
        var signer = Marshal.PtrToStructure<ProviderSigner>(signerPointer);
        var chain = ReadChain(signer);
        CheckRevocation(chain, offlineFixture);
        bool timestamped = false;
        for (uint index = 0; index < signer.CounterSignerCount; index++)
        {
            nint pointer = WTHelperGetProvSignerFromChain(provider, 0, true, index);
            if (pointer == 0)
                continue;
            var counter = Marshal.PtrToStructure<ProviderSigner>(pointer);
            if (counter.Type != 0x10 || counter.Error != 0 || counter.Chain == 0)
                continue;
            CheckRevocation(ReadChain(counter), offlineFixture);
            timestamped = true;
            break;
        }
        if (!timestamped)
            throw new LauncherUpdateException("Setup did not have a trusted timestamp.");

        if (chain.Count != 1 || chain.Chains == 0)
            throw new LauncherUpdateException("Windows did not return a single accepted Setup signing chain.");
        var path = Marshal.PtrToStructure<SimpleChain>(Marshal.ReadIntPtr(chain.Chains));
        if (path.Count < 2 || path.Elements == 0)
            throw new LauncherUpdateException("Setup's signing chain did not lead to a root certificate.");
        var leaf = Marshal.PtrToStructure<ChainElement>(Marshal.ReadIntPtr(path.Elements));
        var root = Marshal.PtrToStructure<ChainElement>(
            Marshal.ReadIntPtr(path.Elements, checked((int)(path.Count - 1) * IntPtr.Size)));
        if (leaf.Certificate == 0 || root.Certificate == 0 || (root.Status.Info & 0x8) == 0) // CERT_TRUST_IS_SELF_SIGNED
            throw new LauncherUpdateException("Setup's signing chain did not end at a root certificate.");

        using var leafCertificate = X509CertificateLoader.LoadCertificate(ReadCertificate(leaf.Certificate));
        using var rootCertificate = X509CertificateLoader.LoadCertificate(ReadCertificate(root.Certificate));
        string rootHash = rootCertificate.GetCertHashString(HashAlgorithmName.SHA256);
        var usages = leafCertificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault()
            ?.EnhancedKeyUsages.Cast<Oid>().Select(usage => usage.Value).ToHashSet();
        if (usages is null || !usages.Contains(CodeSigning) ||
            !Rules.Any(rule => rootHash.Equals(rule.Root, StringComparison.OrdinalIgnoreCase) && usages.Contains(rule.Oid)))
            throw new LauncherUpdateException("Setup's accepted signing root or publisher EKU was unexpected.");
    }

    private static ChainContext ReadChain(ProviderSigner signer) => signer.Chain != 0 && signer.Error == 0
        ? Marshal.PtrToStructure<ChainContext>(signer.Chain)
        : throw new LauncherUpdateException("Windows did not return the accepted Setup certificate chain.");

    private static void CheckRevocation(ChainContext chain, bool offlineFixture)
    {
        if (!offlineFixture && (chain.Status.Error & RevocationUnavailable) != 0)
            throw new LauncherUpdateException("Windows could not establish the revocation status of the Setup signature.");
    }

    private sealed record PublisherRule(string Root, string Oid);

    private static byte[] ReadCertificate(nint certificate)
    {
        var context = Marshal.PtrToStructure<CertificateContext>(certificate);
        var bytes = new byte[context.Length];
        Marshal.Copy(context.Bytes, bytes, 0, bytes.Length);
        return bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustFile
    {
        internal uint Size;
        internal nint Path, Handle, KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        internal uint Size;
        internal nint PolicyData, ClientData;
        internal uint Ui, Revocation, Choice;
        internal nint File;
        internal uint Action;
        internal nint State, UrlReference;
        internal uint Flags, Context;
        internal nint SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderSigner
    {
        internal uint Size, VerifyTimeLow, VerifyTimeHigh, CertificateCount;
        internal nint Certificates;
        internal uint Type;
        internal nint Signer;
        internal uint Error, CounterSignerCount;
        internal nint CounterSigners, Chain;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustStatus
    {
        internal uint Error, Info;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChainContext
    {
        internal uint Size;
        internal TrustStatus Status;
        internal uint Count;
        internal nint Chains;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SimpleChain
    {
        internal uint Size;
        internal TrustStatus Status;
        internal uint Count;
        internal nint Elements;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChainElement
    {
        internal uint Size;
        internal nint Certificate;
        internal TrustStatus Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext
    {
        internal uint Encoding;
        internal nint Bytes;
        internal int Length;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(nint hwnd, [In] in Guid action, ref TrustData data);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperProvDataFromStateData(nint state);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvSignerFromChain(nint data, uint index,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterIndex);
}
