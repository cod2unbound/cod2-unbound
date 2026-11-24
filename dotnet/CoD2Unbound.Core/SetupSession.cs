using System.Text;

namespace CoD2Unbound.Core;

public enum SetupStep { Welcome, Data, Key, Personalize, CrashReports, Ready, Complete, UsageData }
public enum SetupDataState { Missing, Damaged, Found, UnfinishedSteam }
public enum CrashConsentQuestion { Nothing, Answered, Ask }
public sealed record SetupDataChoice(string Path, SetupDataState State, GameArchiveSurvey Survey, string? Problem);
public sealed record SetupPersonalization(string? Name, string Display);

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
    private readonly bool deferWrites;
    private readonly string originalData;
    private readonly bool originallyHadKey;
    private string? pendingKey;
    private string confirmedData;
    private bool canCancel;

    public SetupStep Step { get; private set; }
    public SetupDataChoice Data { get; private set; }
    public bool DataWasPicked { get; private set; }
    public string DataPath => Data.State == SetupDataState.Found ? Data.Path : confirmedData;
    public bool HasStoredKey { get; private set; }
    public bool ReplacingKey { get; private set; }
    public bool CanCancel => canCancel;
    public string? PendingKeyTail => KeyTail(pendingKey);
    public SetupPersonalization? Personalization { get; private set; }
    public bool? CrashChoice { get; private set; }
    public bool? UsageChoice { get; private set; }

    public SetupSession(IGameFiles files, ISettingsFolder folder, ISecretStore secrets, string initialData,
        bool hasStoredKey, bool canCancel = false, SetupStep step = SetupStep.Welcome,
        bool hasReportService = false, Func<string, bool>? unfinishedSteam = null, bool hasUsageService = false,
        bool deferWrites = false)
    {
        this.files = files;
        this.folder = folder;
        this.secrets = secrets;
        this.unfinishedSteam = unfinishedSteam ?? (_ => false);
        this.hasReportService = hasReportService;
        this.hasUsageService = hasUsageService;
        this.canCancel = canCancel;
        this.deferWrites = deferWrites;
        originallyHadKey = HasStoredKey = hasStoredKey;
        originalData = confirmedData = initialData;
        Step = step;
        Data = Inspect(initialData);
    }

    /// <summary>The steps this setup shows, in order: a build without the crash-report or usage
    /// service leaves those questions out, and the step label and capsules count only these.</summary>
    public IReadOnlyList<SetupStep> Steps =>
    [
        SetupStep.Welcome, SetupStep.Data, SetupStep.Key, SetupStep.Personalize,
        .. hasReportService ? [SetupStep.CrashReports] : Array.Empty<SetupStep>(),
        .. hasUsageService ? [SetupStep.UsageData] : Array.Empty<SetupStep>(),
        SetupStep.Ready
    ];

    /// <summary>The one-based position of the current step among <see cref="Steps"/>.</summary>
    public int StepNumber => Math.Max(0, Steps.ToList().IndexOf(Step == SetupStep.Complete ? SetupStep.Ready : Step)) + 1;

    public static string InitialDataPath(string? found, string? remembered, bool setupComplete) =>
        found ?? (setupComplete ? remembered?.Trim() ?? "" : "");

    public static string? KeyTail(string? key) => key is not null && CdKey.IsValid(key) ? CdKey.Normalize(key)[^4..] : null;
    public bool KeyReady(string input) => (HasStoredKey && !ReplacingKey) || CdKey.IsValid(input);
    public void ReplaceKey() => ReplacingKey = true;
    public void KeepStoredKey() => ReplacingKey = false;

    public static CrashConsentQuestion ConsentOwed(bool hasService, bool asked, bool sending) =>
        !hasService || asked ? CrashConsentQuestion.Nothing : sending ? CrashConsentQuestion.Answered : CrashConsentQuestion.Ask;

    public SetupDataChoice ChooseData(string path, bool picked = false)
    {
        Data = Inspect(path);
        DataWasPicked = picked;
        return Data;
    }

    public async ValueTask<bool> ContinueAsync(string keyInput = "", CancellationToken cancellationToken = default,
        Func<ValueTask>? applyReview = null)
    {
        switch (Step)
        {
            case SetupStep.Welcome:
                Step = SetupStep.Data;
                break;
            case SetupStep.Data:
                if (Data.State != SetupDataState.Found || ChooseData(Data.Path, picked: DataWasPicked).State != SetupDataState.Found)
                    return false;
                if (!GameSettings.SafeEngineText(Data.Path) || !GameSettings.SafeEngineText(folder.Root))
                    throw new InvalidDataException("Choose a path without quotes, plus signs, semicolons or control characters.");
                if (!deferWrites)
                    await WriteDataAsync(Data.Path, cancellationToken).ConfigureAwait(false);
                confirmedData = Data.Path;
                Step = SetupStep.Key;
                break;
            case SetupStep.Key:
                if (!KeyReady(keyInput))
                    return false;
                if (ReplacingKey || !HasStoredKey)
                {
                    string normalized = CdKey.Normalize(keyInput);
                    if (deferWrites)
                        pendingKey = normalized;
                    else
                        await secrets.WriteAsync(KeySecret, normalized, cancellationToken).ConfigureAwait(false);
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
                if (deferWrites)
                {
                    // Once application begins, retry the reviewed choices on failure. Cancel can no longer discard them.
                    canCancel = false;
                    if (applyReview is not null)
                        await applyReview().ConfigureAwait(false);
                    await WriteDataAsync(confirmedData, cancellationToken).ConfigureAwait(false);
                    if (pendingKey is { } key)
                        await secrets.WriteAsync(KeySecret, key, cancellationToken).ConfigureAwait(false);
                    pendingKey = null;
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

    public bool ReviewPersonalization(SetupPersonalization? choice)
    {
        if (Step != SetupStep.Personalize)
            return false;
        if (choice is { } edit && (edit.Display is not ("borderless" or "exclusive" or "windowed") ||
            edit.Name is { } name && SetupPlayer.NameProblem(name) is not null))
            return false;
        Personalization = choice;
        return PersonalizationSaved();
    }

    public void ReviewPrivacy(bool send)
    {
        switch (Step)
        {
            case SetupStep.CrashReports:
                CrashChoice = send;
                CrashQuestionAnswered();
                break;
            case SetupStep.UsageData:
                UsageChoice = send;
                UsageQuestionAnswered();
                break;
        }
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
        if (deferWrites)
        {
            pendingKey = null;
            Personalization = null;
            CrashChoice = UsageChoice = null;
            HasStoredKey = originallyHadKey;
            confirmedData = originalData;
        }
        ChooseData(confirmedData);
        ReplacingKey = false;
        canCancel = false;
        Step = SetupStep.Complete;
        return true;
    }

    private ValueTask WriteDataAsync(string path, CancellationToken cancellationToken) =>
        folder.WriteAsync(DataPathFile, [.. Encoding.UTF8.GetBytes(path + "\n")], cancellationToken);

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
