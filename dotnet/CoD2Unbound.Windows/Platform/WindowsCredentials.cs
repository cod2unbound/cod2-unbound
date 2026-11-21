using System.ComponentModel;
using System.Runtime.InteropServices;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsCredentials : ISecretStore
{
    private const uint Generic = 1;
    private const uint LocalMachine = 2; // Persists for this Windows user on this PC.
    private const int NotFound = 1168;

    public ValueTask<string?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CredRead(Target(name), Generic, 0, out nint pointer))
        {
            int error = Marshal.GetLastPInvokeError();
            return error == NotFound ? ValueTask.FromResult<string?>(null) : throw Failure(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return ValueTask.FromResult<string?>(Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / 2)));
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public ValueTask WriteAsync(string name, string? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string target = Target(name);
        if (value is null)
        {
            if (!CredDelete(target, Generic, 0))
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != NotFound)
                    throw Failure(error);
            }
            return ValueTask.CompletedTask;
        }

        nint blob = Marshal.StringToCoTaskMemUni(value);
        try
        {
            var credential = new Credential
            {
                Type = Generic, TargetName = target, BlobSize = checked((uint)value.Length * 2),
                Blob = blob, Persist = LocalMachine,
            };
            if (!CredWrite(in credential, 0))
                throw Failure(Marshal.GetLastPInvokeError());
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
        return ValueTask.CompletedTask;
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
