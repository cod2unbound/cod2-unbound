using System.Text;

namespace CoD2Unbound.Core;

public enum SetupStep { Welcome, Data, Key, Personalize, CrashReports, Ready, Complete, UsageData }
public enum SetupDataState { Missing, Damaged, Found, UnfinishedSteam }
public enum CrashConsentQuestion { Nothing, Answered, Ask }
public sealed record SetupDataChoice(string Path, SetupDataState State, GameArchiveSurvey Survey, string? Problem);

/// <summary>One setup attempt. The presentation host serializes calls, including asynchronous transitions.</summary>
public sealed class SetupSession
{
    public const string DataPathFile = "data-path.txt";
    public const string CompletionFile = ".launcher-setup-complete";
    public const string KeySecret = "cod2-cd-key";
    public const string SteamUnfinishedMessage =
        "This folder holds a Steam download that didn’t finish. Finish the download, or pick another folder.";
    public const string FoundSummary = "All 16 game archives present · Version 1.3";

    private readonly IGameFiles files;
    private readonly ISettingsFolder folder;
    private readonly ISecretStore secrets;
    private readonly Func<string, bool> unfinishedSteam;
    private readonly bool hasReportService;
    private readonly bool hasUsageService;
    private string confirmedData;
    private bool canCancel;

    public SetupStep Step { get; private set; }
    public SetupDataChoice Data { get; private set; }
    public string DataPath => Data.State == SetupDataState.Found ? Data.Path : confirmedData;
    public bool HasStoredKey { get; private set; }
    public bool ReplacingKey { get; private set; }
    public bool CanCancel => canCancel;

    public SetupSession(IGameFiles files, ISettingsFolder folder, ISecretStore secrets, string initialData,
        bool hasStoredKey, bool canCancel = false, SetupStep step = SetupStep.Welcome,
        bool hasReportService = false, Func<string, bool>? unfinishedSteam = null, bool hasUsageService = false)
    {
        this.files = files;
        this.folder = folder;
        this.secrets = secrets;
        this.unfinishedSteam = unfinishedSteam ?? (_ => false);
        this.hasReportService = hasReportService;
        this.hasUsageService = hasUsageService;
        this.canCancel = canCancel;
        HasStoredKey = hasStoredKey;
        confirmedData = initialData;
        Step = step;
        Data = Inspect(initialData);
    }

    public static string InitialDataPath(string? found, string? remembered, bool setupComplete) =>
        found ?? (setupComplete ? remembered?.Trim() ?? "" : "");

    public static string? KeyTail(string? key) => key is not null && CdKey.IsValid(key) ? CdKey.Normalize(key)[^4..] : null;
    public bool KeyReady(string input) => (HasStoredKey && !ReplacingKey) || CdKey.IsValid(input);
    public void ReplaceKey() => ReplacingKey = true;
    public void KeepStoredKey() => ReplacingKey = false;

    public static CrashConsentQuestion ConsentOwed(bool hasService, bool asked, bool sending) =>
        !hasService || asked ? CrashConsentQuestion.Nothing : sending ? CrashConsentQuestion.Answered : CrashConsentQuestion.Ask;

    public SetupDataChoice ChooseData(string path) => Data = Inspect(path);

    public async ValueTask<bool> ContinueAsync(string keyInput = "", CancellationToken cancellationToken = default)
    {
        switch (Step)
        {
            case SetupStep.Welcome:
                Step = SetupStep.Data;
                break;
            case SetupStep.Data:
                if (Data.State != SetupDataState.Found || ChooseData(Data.Path).State != SetupDataState.Found)
                    return false;
                if (!GameSettings.SafeEngineText(Data.Path) || !GameSettings.SafeEngineText(folder.Root))
                    throw new InvalidDataException("Choose a path without quotes, plus signs, semicolons or control characters.");
                await folder.WriteAsync(DataPathFile, [.. Encoding.UTF8.GetBytes(Data.Path + "\n")], cancellationToken)
                    .ConfigureAwait(false);
                confirmedData = Data.Path;
                Step = SetupStep.Key;
                break;
            case SetupStep.Key:
                if (!KeyReady(keyInput))
                    return false;
                if (ReplacingKey || !HasStoredKey)
                {
                    await secrets.WriteAsync(KeySecret, CdKey.Normalize(keyInput), cancellationToken).ConfigureAwait(false);
                    HasStoredKey = true;
                    ReplacingKey = false;
                }
                Step = SetupStep.Personalize;
                break;
            case SetupStep.Ready:
                if (ChooseData(confirmedData).State != SetupDataState.Found)
                {
                    Step = SetupStep.Data;
                    return false;
                }
                await folder.WriteAsync(CompletionFile, [.. "prepared\n"u8.ToArray()], cancellationToken).ConfigureAwait(false);
                canCancel = false;
                Step = SetupStep.Complete;
                break;
            default:
                return false;
        }
        return true;
    }

    public bool SkipKey()
    {
        if (Step != SetupStep.Key)
            return false;
        KeepStoredKey();
        Step = SetupStep.Personalize;
        return true;
    }

    // The host applies the reviewed player-name/display changes through its profile/settings stores.
    public bool PersonalizationSaved()
    {
        if (Step != SetupStep.Personalize)
            return false;
        Step = hasReportService ? SetupStep.CrashReports : AfterCrashQuestion;
        return true;
    }

    public async ValueTask<GameSettings?> AnswerCrashReportsAsync(GameSettings current, bool send,
        CancellationToken cancellationToken = default)
    {
        if (Step != SetupStep.CrashReports)
            return null;
        var updated = current with { SendCrashReports = send, CrashReportsAsked = true };
        await new GameSettingsStorage(folder).WriteAsync(updated, cancellationToken).ConfigureAwait(false);
        CrashQuestionAnswered();
        return updated;
    }

    public void CrashQuestionAnswered()
    {
        if (Step == SetupStep.CrashReports)
            Step = AfterCrashQuestion;
    }

    public void UsageQuestionAnswered()
    {
        if (Step == SetupStep.UsageData)
            Step = SetupStep.Ready;
    }

    private SetupStep AfterCrashQuestion => hasUsageService ? SetupStep.UsageData : SetupStep.Ready;

    public bool Back()
    {
        Step = Step switch
        {
            SetupStep.Data => SetupStep.Welcome,
            SetupStep.Key => SetupStep.Data,
            SetupStep.Personalize => SetupStep.Key,
            SetupStep.CrashReports => SetupStep.Personalize,
            SetupStep.UsageData => hasReportService ? SetupStep.CrashReports : SetupStep.Personalize,
            SetupStep.Ready => hasUsageService ? SetupStep.UsageData : hasReportService ? SetupStep.CrashReports : SetupStep.Personalize,
            _ => Step
        };
        return Step != SetupStep.Complete;
    }

    public bool Cancel()
    {
        if (!canCancel)
            return false;
        Data = Inspect(confirmedData);
        ReplacingKey = false;
        canCancel = false;
        Step = SetupStep.Complete;
        return true;
    }

    private SetupDataChoice Inspect(string path)
    {
        var empty = new GameArchiveSurvey(0, GameArchives.Required);
        if (string.IsNullOrWhiteSpace(path))
            return new(path, SetupDataState.Missing, empty, null);
        if (unfinishedSteam(path))
            return new(path, SetupDataState.UnfinishedSteam, empty, SteamUnfinishedMessage);
        var survey = GameArchives.Survey(files, path);
        if (survey.IsComplete)
            return new(path, SetupDataState.Found, survey, null);
        int more = survey.Missing.Length - 1;
        string missing = more > 0 ? $"{survey.Missing[0]} and {more} more game archive{(more == 1 ? "" : "s")}" : survey.Missing[0];
        return new(path, SetupDataState.Damaged, survey, $"This folder is missing {missing}. " +
            "Choose the Call of Duty 2 folder itself, the one that contains “main” with iw_00.iwd through iw_15.iwd.");
    }
}
