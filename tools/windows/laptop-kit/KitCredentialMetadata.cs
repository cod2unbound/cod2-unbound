using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

public static class KitCredentialMetadata
{
    private const string Target = "CoD2 Unbound/CD Key";
    private const int NotFound = 1168;
    private const uint Generic = 1;

    public static string ReadPublicKeyStamp()
    {
        uint count;
        IntPtr entries;
        if (!CredEnumerate(Target + "*", 0, out count, out entries))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NotFound) return "absent";
            throw new Win32Exception(error);
        }

        try
        {
            for (int i = 0; i < count; ++i)
            {
                IntPtr entry = Marshal.ReadIntPtr(entries, checked(i * IntPtr.Size));
                var metadata = (Metadata)Marshal.PtrToStructure(entry, typeof(Metadata));
                if (metadata.Type == Generic && string.Equals(Marshal.PtrToStringUni(metadata.TargetName),
                    Target, StringComparison.OrdinalIgnoreCase))
                    return "present " + metadata.LastWritten.ToString(CultureInfo.InvariantCulture);
            }
            return "absent";
        }
        finally { CredFree(entries); }
    }

    // Only marshal the prefix through LastWritten. The credential blob is never accessed.
    [StructLayout(LayoutKind.Sequential)]
    private struct Metadata
    {
        public uint Flags, Type;
        public IntPtr TargetName, Comment;
        public long LastWritten;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerate(string filter, uint flags, out uint count, out IntPtr entries);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern void CredFree(IntPtr entries);
}
