using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CoD2Unbound.Core;
using Microsoft.Win32.SafeHandles;

namespace CoD2Unbound.Windows.Platform;

internal static class SetupPublisher
{
    private static readonly Guid Authenticode = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    private static readonly string[] Pins = typeof(SetupPublisher).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Where(attribute => attribute.Key == "UpdatePublisherCertificateSha256")
        .SelectMany(attribute => (attribute.Value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        .Where(value => value.Length == 64 && value.All(char.IsAsciiHexDigit)).ToArray();

    internal static bool Configured => Pins.Length > 0;

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

            nint provider = WTHelperProvDataFromStateData(data.State);
            nint signer = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            nint entry = signer == 0 ? 0 : WTHelperGetProvCertFromChain(signer, 0);
            nint cert = entry == 0 ? 0 : Marshal.PtrToStructure<ProviderCertificate>(entry).Certificate;
            if (cert == 0)
                throw new LauncherUpdateException("Windows could not identify who signed Setup.");
            // Use the leaf Windows actually accepted, including when the file has multiple signatures.
            using var certificate = X509CertificateLoader.LoadCertificate(ReadCertificate(cert));
            string sha256 = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            if (!Pins.Contains(sha256, StringComparer.OrdinalIgnoreCase))
                throw new LauncherUpdateException("Setup was signed by an unexpected publisher.");
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
    private struct ProviderCertificate
    {
        internal uint Size;
        internal nint Certificate;
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
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvCertFromChain(nint signer, uint index);
}
