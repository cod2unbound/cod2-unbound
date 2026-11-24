#if UPDATE_FIXTURES
using CoD2Unbound.Core;
using Microsoft.UI.Xaml.Automation.Peers;

namespace CoD2Unbound.Windows.Views;

/// <summary>Fixture builds only. COD2_UNBOUND_TEST_SETUP_STATE walks a fresh Setup to one board state with
/// the session's own transitions, over fixture folders under COD2_UNBOUND_TEST_SETUP_ROOT, so each state can
/// be pictured beside its board. Public builds compile none of this.</summary>
public sealed partial class SetupPage
{
    private static bool previewed;
    private const string SampleKey = "000000000000000086D3";

    private async Task PreviewAsync()
    {
        if (previewed || Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_SETUP_STATE") is not { Length: > 0 } state ||
            Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_SETUP_ROOT") is not { Length: > 0 } root)
            return;
        previewed = true;
        string steam = Path.Join(root, @"Steam\steamapps\common\Call of Duty 2");
        string disc = Path.Join(root, @"Games\Call of Duty 2");
        var steamCopy = new GameInstallation(steam, [], GameInstallSource.Steam);
        var discCopy = new GameInstallation(disc, [], GameInstallSource.Disc);
        await shell.RunAsync(async () =>
        {
            if (state == "welcome")
                return;
            await Setup.ContinueAsync();
            switch (state)
            {
                case "data-missing" or "data-dragging" or "data-waiting" or "data-nosteam":
                    session.PreviewSetupCopies([]);
                    Setup.ChooseData("");
                    if (state == "data-waiting")
                        steamWatch = new CancellationTokenSource();
                    steamUnavailable = state == "data-nosteam";
                    await RefreshAsync();
                    if (state == "data-dragging")
                        ShowDropTarget(true);
                    return;
                case "data-wrong" or "data-unfinished":
                    session.PreviewSetupCopies([]);
                    Setup.ChooseData(state == "data-wrong" ? Path.Join(root, @"Downloads\cod2-backup")
                        : Path.Join(root, @"SteamLibrary\steamapps\common\Call of Duty 2"), picked: true);
                    await RefreshAsync();
                    return;
                case "data-copies":
                    session.PreviewSetupCopies([steamCopy, discCopy]);
                    Setup.ChooseData(steam);
                    await RefreshAsync();
                    return;
            }
            bool withDisc = state is "key-disc" or "key-nodisc";
            session.PreviewSetupCopies(withDisc ? [steamCopy, discCopy] : [steamCopy]);
            Setup.ChooseData(steam);
            if (state == "data-found")
            {
                await RefreshAsync();
                return;
            }
            await Setup.ContinueAsync();
            if (state.StartsWith("key", StringComparison.Ordinal))
            {
                if (state == "key-stored")
                {
                    await Setup.ContinueAsync(SampleKey);
                    Setup.Back();
                }
                discKeyMissing = state == "key-nodisc";
                await RefreshAsync();
                if (keyEditor is not null)
                    keyEditor.Value = state switch
                    {
                        "key-typing" => "0000 0000 0000 0",
                        "key-mismatch" => "000000000000000086D4",
                        "key-verified" => SampleKey,
                        _ => ""
                    };
                return;
            }
            if (state.EndsWith("skipped", StringComparison.Ordinal))
                Setup.SkipKey();
            else
                await Setup.ContinueAsync(SampleKey);
            if (state.StartsWith("yours", StringComparison.Ordinal))
            {
                await RefreshAsync();
                if (nameEditor is not null)
                    nameEditor.NameText = "^3Sgt. ^7Morrow";
                if (state == "yours-colours")
                    nameEditor?.OpenColours();
                UpdateContinue();
                return;
            }
            Setup.ReviewPersonalization(new("^3Sgt. ^7Morrow", "borderless"));
            if (state.StartsWith("crash", StringComparison.Ordinal) && Setup.Step == SetupStep.CrashReports)
            {
                await RefreshAsync();
                return;
            }
            if (Setup.Step == SetupStep.CrashReports)
                Setup.ReviewPrivacy(true);
            if (state.StartsWith("usage", StringComparison.Ordinal) && Setup.Step == SetupStep.UsageData)
            {
                await RefreshAsync();
                return;
            }
            if (Setup.Step == SetupStep.UsageData)
                Setup.ReviewPrivacy(false);
            await RefreshAsync();
        });
        if (state is "crash-preview" or "usage-preview" && previewLink is not null)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                (FrameworkElementAutomationPeer.FromElement(previewLink) as ButtonAutomationPeer
                    ?? new ButtonAutomationPeer(previewLink)).Invoke());
    }
}
#endif
