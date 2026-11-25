using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using CoD2Unbound.Core;
using Microsoft.Win32;

namespace CoD2Unbound.Windows.Platform;

internal static class WindowsInstallRecovery
{
    // The previous directory is the journal.
    private const string Previous = ".previous-install";
    private const string Incomplete = ".incomplete-install";
    internal static string StandardRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", WindowsInstallIdentity.Product);

    internal static bool NeedsRestore(string root) => Directory.Exists(root + Previous);

    internal static bool RestoreFailed(string root) =>
        IsStandardRoot(root) && NeedsRestore(root) && ReadTransactionValue("RestoreFailed") == "1";

    private static bool IsStandardRoot(string root) =>
        string.Equals(root, StandardRoot, StringComparison.OrdinalIgnoreCase);

    private static string? ReadTransactionValue(string name)
    {
        using var registry = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var transaction = registry.OpenSubKey(WindowsInstallIdentity.TransactionKey);
        return transaction?.GetValue(name) as string;
    }

    internal static bool RedirectInterruptedStart()
    {
        string root = StandardRoot;
        string actual = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        if (!IsStandardRoot(actual) || (!NeedsRestore(root) && !Directory.Exists(root + Incomplete)))
            return false;

        try
        {
            using var parent = new WindowsFiles.DirectoryHandles(Path.GetDirectoryName(root)!);
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User ?? throw new IOException("Windows did not identify the current user.");
            using var transaction = new Mutex(false, WindowsInstallIdentity.TransactionMutex + sid.Value);
            try
            {
                if (!transaction.WaitOne(0))
                    return NeedsRestore(root); // Setup will reopen the launcher.
            }
            catch (AbandonedMutexException) { }
            try
            {
                CleanIncomplete(root);
                if (!NeedsRestore(root) || RestoreFailed(root))
                    return false;

                if (ReadTransactionValue("Attempt") is { } attempt &&
                    WindowsLauncherUpdate.StartRecovery(root, WindowsLauncherSession.ResolveHome(), attempt))
                    return true;
            }
            finally { transaction.ReleaseMutex(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            Win32Exception or InvalidOperationException or LauncherUpdateException)
        {
            if (!NeedsRestore(root))
                return false;
        }
        ShowRepairDialog();
        return true;
    }

    private static void CleanIncomplete(string root)
    {
        string incomplete = root + Incomplete;
        try { Directory.Delete(incomplete, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { Directory.Move(incomplete, incomplete + "-" + Guid.NewGuid().ToString("N")); }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void ShowRepairDialog()
    {
        const int download = 100;
        const int close = 2;
        nint downloadText = Marshal.StringToHGlobalUni("Open download page");
        nint closeText = Marshal.StringToHGlobalUni("Close");
        int buttonSize = Marshal.SizeOf<TaskDialogButton>();
        nint buttons = Marshal.AllocHGlobal(2 * buttonSize);
        try
        {
            Marshal.StructureToPtr(new TaskDialogButton { Id = download, Text = downloadText }, buttons, false);
            Marshal.StructureToPtr(new TaskDialogButton { Id = close, Text = closeText }, buttons + buttonSize, false);
            var dialog = new TaskDialogConfig
            {
                Size = (uint)Marshal.SizeOf<TaskDialogConfig>(),
                Flags = 0x8, // TDF_ALLOW_DIALOG_CANCELLATION
                Title = "CoD2 Unbound",
                MainInstruction = "The last update didn't finish.",
                Content = "Run Setup again to repair CoD2 Unbound.",
                ButtonCount = 2,
                Buttons = (nint)buttons,
                DefaultButton = close
            };
            if (TaskDialogIndirect(in dialog, out int button, 0, 0) == 0 && button == download)
            {
                try
                {
                    using var page = Process.Start(new ProcessStartInfo(WindowsReleaseAssets.ReleasesPage.AbsoluteUri)
                        { UseShellExecute = true });
                }
                catch (Win32Exception) { }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buttons);
            Marshal.FreeHGlobal(closeText);
            Marshal.FreeHGlobal(downloadText);
        }
    }

    // CommCtrl.h packs the TaskDialog structures to one byte.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TaskDialogButton
    {
        internal int Id;
        internal nint Text;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    private struct TaskDialogConfig
    {
        internal uint Size;
        internal nint Parent, Instance;
        internal uint Flags, CommonButtons;
        internal string Title;
        internal nint MainIcon;
        internal string MainInstruction, Content;
        internal uint ButtonCount;
        internal nint Buttons;
        internal int DefaultButton;
        internal uint RadioButtonCount;
        internal nint RadioButtons;
        internal int DefaultRadioButton;
        internal nint VerificationText, ExpandedInformation, ExpandedControlText, CollapsedControlText;
        internal nint FooterIcon, Footer, Callback, CallbackData;
        internal uint Width;
    }

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern int TaskDialogIndirect(in TaskDialogConfig config, out int button, nint radio, nint verified);
}
