using System.ComponentModel;
using System.Runtime.InteropServices;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsCredentials : ISecretStore
{
    private const uint Generic = 1;
    private const uint LocalMachine = 2; // Persists for this Windows user on this PC.
    private const int NotFound = 1168;
    private const string EngineCdKey = "CoD2 Unbound/CD Key";
    private const uint CdKeyBytes = 20;

    private readonly string? testKeyTarget = ResolveTestSlot() is { } slot ? $"{EngineCdKey} (test {slot})" : null;

    internal static string? ResolveTestSlot()
    {
#if UPDATE_FIXTURES
        string? slot = Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_KEY_SLOT");
        if (slot is { Length: >= 1 and <= 32 } &&
            slot.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            return slot;
#endif
        return null;
    }

    public ValueTask<string?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string target = Target(name);
        if (name != SetupSession.KeySecret)
            return ValueTask.FromResult(Read(target, ascii: false).Value);

        var (found, key) = Read(testKeyTarget ?? EngineCdKey, ascii: true);
        if (found || testKeyTarget is not null)
            return ValueTask.FromResult(key is not null && CdKey.IsValid(key) ? key : null);

        // Earlier launchers stored the CD key under the core name in UTF-16.
        if (Read(target, ascii: false).Value is not { } previous || !CdKey.IsValid(previous))
            return ValueTask.FromResult<string?>(null);

        key = CdKey.Normalize(previous);
        Write(EngineCdKey, key, ascii: true);
        return ValueTask.FromResult<string?>(key);
    }

    public ValueTask WriteAsync(string name, string? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (name == SetupSession.KeySecret && testKeyTarget is { } testTarget)
        {
            if (value is null)
                Delete(testTarget);
            else if (!CdKey.IsValid(value))
                throw new ArgumentException("The CD key is invalid.", nameof(value));
            else
                Write(testTarget, CdKey.Normalize(value), ascii: true);
            return ValueTask.CompletedTask;
        }
        string target = Target(name);
        bool cdKey = name == SetupSession.KeySecret;
        if (value is null)
        {
            Delete(target);
            if (cdKey)
                Delete(EngineCdKey);
        }
        else if (cdKey)
        {
            if (!CdKey.IsValid(value))
                throw new ArgumentException("The CD key is invalid.", nameof(value));

            Write(EngineCdKey, CdKey.Normalize(value), ascii: true);
        }
        else
        {
            Write(target, value, ascii: false);
        }
        return ValueTask.CompletedTask;
    }

    private static (bool Found, string? Value) Read(string target, bool ascii)
    {
        if (!CredRead(target, Generic, 0, out nint pointer))
        {
            int error = Marshal.GetLastPInvokeError();
            return error == NotFound ? (false, null) : throw Failure(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (!ascii)
                return (true, Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / 2)));

            // The engine requires exactly twenty ASCII bytes without a terminator.
            return (true, credential.BlobSize == CdKeyBytes ? Marshal.PtrToStringAnsi(credential.Blob, (int)CdKeyBytes) : null);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    private static void Delete(string target)
    {
        if (!CredDelete(target, Generic, 0))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != NotFound)
                throw Failure(error);
        }
    }

    private static void Write(string target, string value, bool ascii)
    {
        nint blob = ascii ? Marshal.StringToCoTaskMemAnsi(value) : Marshal.StringToCoTaskMemUni(value);
        try
        {
            var credential = new Credential
            {
                Type = Generic, TargetName = target, BlobSize = ascii ? CdKeyBytes : checked((uint)value.Length * 2),
                Blob = blob, Persist = LocalMachine,
            };
            if (!CredWrite(in credential, 0))
                throw Failure(Marshal.GetLastPInvokeError());
        }
        finally
        {
            if (ascii)
                Marshal.ZeroFreeCoTaskMemAnsi(blob);
            else
                Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    private static string Target(string name) => "CoD2 Unbound/" + name;
    private static IOException Failure(int error) => new("Windows Credential Manager could not access the key.", new Win32Exception(error));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        internal uint Flags, Type;
        internal string TargetName;
        internal nint Comment;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        internal uint BlobSize;
        internal nint Blob;
        internal uint Persist, AttributeCount;
        internal nint Attributes, TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(in Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern void CredFree(nint credential);
}
