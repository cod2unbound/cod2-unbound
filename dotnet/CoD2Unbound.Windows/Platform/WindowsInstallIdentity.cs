namespace CoD2Unbound.Windows.Platform;

// Keep the product, registry and mutex names in sync with unbound.iss.
internal static class WindowsInstallIdentity
{
#if UPDATE_FIXTURES
    internal const string Product = "CoD2 Unbound Test";
    private const string ProductId = "CoD2UnboundTest";
    internal const string AppId = "CoD2Unbound.Launcher.Fixture";
#else
    internal const string Product = "CoD2 Unbound";
    private const string ProductId = "CoD2Unbound";
    internal const string AppId = "CoD2Unbound.Launcher";
#endif
    internal const string RestoreDescription = Product + " Setup with recovery";
    internal const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + ProductId + "_is1";
    internal const string TransactionKey = @"Software\" + ProductId + @"\InstallTransaction";
    internal const string TransactionMutex = @"Local\" + ProductId + ".InstallTransaction-";
}
