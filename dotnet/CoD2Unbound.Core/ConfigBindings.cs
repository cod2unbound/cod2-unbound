using System.Collections.Immutable;
using System.Text;

namespace CoD2Unbound.Core;

public readonly record struct EngineKey : IComparable<EngineKey>
{
    public EngineKey(int number)
    {
        if (number is < 1 or > 255)
            throw new ArgumentOutOfRangeException(nameof(number));
        Number = number is >= 65 and <= 90 ? number + 32 : number;
    }

    public int Number { get; }
    public string Spelling => ConfigDocument.KeySpelling(Number);
    public bool Reserved => Number is 96 or 126;
    public static EngineKey? Parse(string spelling) => ConfigDocument.KeyNumber(spelling) is int number ? new EngineKey(number) : null;
    public int CompareTo(EngineKey other) => Number.CompareTo(other.Number);
    public static bool operator <(EngineKey left, EngineKey right) => left.CompareTo(right) < 0;
    public static bool operator <=(EngineKey left, EngineKey right) => left.CompareTo(right) <= 0;
    public static bool operator >(EngineKey left, EngineKey right) => left.CompareTo(right) > 0;
    public static bool operator >=(EngineKey left, EngineKey right) => left.CompareTo(right) >= 0;
}

public static class EngineInputMapping
{
    private const int ScanCodeMask = 1 << 30;
    private static readonly Dictionary<int, int> ScanCodes = new()
    {
        [158] = 13, [82] = 154, [81] = 155, [80] = 156, [79] = 157,
        [226] = 158, [230] = 158, [224] = 159, [228] = 159,
        [225] = 160, [229] = 160, [227] = 150, [231] = 150,
        [57] = 151, [72] = 153, [73] = 161,
        [78] = 163, [75] = 164, [74] = 165, [77] = 166,
        [95] = 182, [96] = 183, [97] = 184, [92] = 185, [93] = 186,
        [94] = 187, [89] = 188, [90] = 189, [91] = 190, [88] = 191,
        [98] = 192, [99] = 193, [84] = 194, [86] = 195, [87] = 196,
        [83] = 197, [85] = 198, [103] = 199
    };

    public static int? FromSdlKey(int code)
    {
        if (code >= ScanCodeMask)
        {
            int scan = code ^ ScanCodeMask;
            if (scan is >= 58 and <= 69)
                return 167 + scan - 58;
            if (scan is >= 104 and <= 106)
                return 179 + scan - 104;
            return ScanCodes.TryGetValue(scan, out int found) ? found : null;
        }
        return code switch { 8 => 127, 127 => 162, 9 or 13 or 27 or >= 32 and < 127 => code, _ => null };
    }

    public static int? FromSdlButton(int button) => button switch { 1 => 200, 2 => 202, 3 => 201, 4 => 203, 5 => 204, _ => null };
    public static int? FromWheel(double delta) => !double.IsFinite(delta) || delta == 0 ? null : delta > 0 ? 206 : 205;
}

public enum ConfigBindingGroup { Movement, Combat, Communication, GameUI }
public enum ConfigBindingSlot { Primary, Secondary }
public enum ConfigBindingFailure { UnavailableAction, ReservedKey, DuplicateActionKey, ConflictNeedsReview, Locked }

public sealed class ConfigBindingException(ConfigBindingFailure failure, string? locked = null) : InvalidOperationException(failure switch
{
    ConfigBindingFailure.UnavailableAction => "This action is not available in this game build.",
    ConfigBindingFailure.ReservedKey => "This key opens the console and cannot be reassigned.",
    ConfigBindingFailure.DuplicateActionKey => "This key already runs this action.",
    ConfigBindingFailure.ConflictNeedsReview => "This key already has a binding. Review it before replacing it.",
    ConfigBindingFailure.Locked => $"Unlock {locked} before changing this binding.",
    _ => throw new ArgumentOutOfRangeException(nameof(failure))
})
{
    public ConfigBindingFailure Failure { get; } = failure;
}

public sealed record ConfigBindingAction(string Name, string Title, string Summary, ConfigBindingGroup Group);
public sealed record ConfigBindingEntry(EngineKey Key, ImmutableArray<byte> Bytes, string? Action, bool HasReleaseTail)
{
    public string Text => Encoding.Latin1.GetString(Bytes.AsSpan());
}
public sealed record ConfigBindingRow(ConfigBindingAction Action, ImmutableArray<EngineKey> Keys)
{
    public EngineKey? Primary => Keys.IsEmpty ? null : Keys[0];
    public EngineKey? Secondary => Keys.Length < 2 ? null : Keys[1];
    public ImmutableArray<EngineKey> Additional => Keys.Length <= 2 ? [] : Keys[2..];
}

public sealed class ConfigBindingEdit
{
    internal ConfigBindingEdit(string profileId, ConfigBindingAction? action, EngineKey? oldKey, EngineKey? key,
        ConfigBindingEntry? conflict, ConfigProfileVersion before, ConfigDocument document)
    {
        ProfileId = profileId;
        Action = action;
        OldKey = oldKey;
        Key = key;
        Conflict = conflict;
        Before = before;
        Document = document;
    }

    public string ProfileId { get; }
    public ConfigBindingAction? Action { get; }
    public EngineKey? OldKey { get; }
    public EngineKey? Key { get; }
    public ConfigBindingEntry? Conflict { get; }
    public ConfigProfileVersion Before { get; }
    internal ConfigDocument Document { get; }
    public ImmutableArray<byte> Preview => Document.Serialized;
    public bool HasChanges => !Preview.AsSpan().SequenceEqual(Before.Config.AsSpan());
    public string Summary => Action is not null ? $"Changed {Action.Title} binding" :
        Key is { } key ? $"Changed {key.Spelling} binding" : $"Cleared {OldKey?.Spelling ?? "key"} binding";
}

public sealed class ConfigBindings
{
    private readonly ConfigDocument document;
    public ImmutableArray<ConfigBindingAction> Actions { get; }
    public ImmutableArray<ConfigBindingEntry> Entries { get; }

    public ConfigBindings(ConfigDocument document, ConfigCatalog catalog, ConfigCatalogMode mode,
        IReadOnlyDictionary<string, string>? labels = null)
    {
        this.document = document;
        Actions = [.. catalog.Commands.Where(command => command.Bindable && command.Variant(mode) is not null &&
                !command.Name.StartsWith('-'))
            .Select(command => (Command: command, Title: labels?.GetValueOrDefault(command.Name) ?? command.MenuLabel))
            .Where(item => !string.IsNullOrEmpty(item.Title))
            .Select(item => new ConfigBindingAction(item.Command.Name, item.Title!, item.Command.Summary, Group(item.Command.Name.ToLowerInvariant())))
            .OrderBy(action => action.Name, StringComparer.Ordinal)];
        var names = Actions.ToDictionary(action => action.Name, action => action.Name, StringComparer.OrdinalIgnoreCase);
        var entries = ImmutableArray.CreateBuilder<ConfigBindingEntry>();
        foreach (var key in Enumerable.Range(1, 255).Select(number => new EngineKey(number)).Distinct().Order())
        {
            if (document.EffectiveBindBytes(key.Spelling) is not { IsEmpty: false } value)
                continue;
            string text = Encoding.Latin1.GetString(value.AsSpan());
            entries.Add(new(key, value, names.GetValueOrDefault(text), value[0] == '+' && value.Contains((byte)';')));
        }
        Entries = entries.ToImmutable();
    }

    public ConfigBindingRow? Row(string name)
    {
        var action = Actions.FirstOrDefault(action => action.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return action is null ? null : new(action, [.. Entries.Where(entry => entry.Action == action.Name).Select(entry => entry.Key)]);
    }

    public ConfigBindingEdit Prepare(ConfigProfileRead profile, string action, ConfigBindingSlot slot, EngineKey? key)
    {
        ValidateSource(profile);
        var row = Row(action) ?? throw new ConfigBindingException(ConfigBindingFailure.UnavailableAction);
        var old = slot == ConfigBindingSlot.Primary ? row.Primary : row.Secondary;
        return PrepareRow(profile, row, old, key);
    }

    public ConfigBindingEdit PrepareAdditional(ConfigProfileRead profile, string action, EngineKey old, EngineKey key)
    {
        ValidateSource(profile);
        var row = Row(action) ?? throw new ConfigBindingException(ConfigBindingFailure.UnavailableAction);
        if (!row.Keys.Contains(old))
            throw new ConfigProfileException(ConfigProfileFailure.Conflict);
        return PrepareRow(profile, row, old, key);
    }

    private ConfigBindingEdit PrepareRow(ConfigProfileRead profile, ConfigBindingRow row, EngineKey? old, EngineKey? key)
    {
        ValidateKey(key);
        if (key is { } chosen && key != old && row.Keys.Contains(chosen))
            throw new ConfigBindingException(ConfigBindingFailure.DuplicateActionKey);

        var changed = document;
        if (key != old)
        {
            if (old is { } previous)
                changed = changed.RemoveBind(previous.Spelling);
            if (key is { } next)
                changed = changed.SetBind(next.Spelling, row.Action.Name);
        }
        var edit = new ConfigBindingEdit(profile.Profile.Id, row.Action, old, key,
            key == old ? null : Entries.FirstOrDefault(entry => entry.Key == key), profile.Version, changed);
        CheckLocks(edit, profile);
        return edit;
    }

    public ConfigBindingEdit PrepareMove(ConfigProfileRead profile, EngineKey old, EngineKey key)
    {
        ValidateSource(profile);
        ValidateKey(key);
        var previous = Entries.FirstOrDefault(entry => entry.Key == old) ??
            throw new ConfigProfileException(ConfigProfileFailure.Conflict);
        var action = previous.Action is { } name ? Row(name) : null;
        if (action is not null)
            return PrepareRow(profile, action, old, key);
        var updated = key == old ? document : document.RemoveBind(old.Spelling).SetBind(key.Spelling, previous.Bytes.AsSpan());
        var edit = new ConfigBindingEdit(profile.Profile.Id, null, old, key,
            key == old ? null : Entries.FirstOrDefault(entry => entry.Key == key), profile.Version, updated);
        CheckLocks(edit, profile);
        return edit;
    }

    public ConfigBindingEdit PrepareKey(ConfigProfileRead profile, EngineKey key, ImmutableArray<byte>? binding)
    {
        ValidateSource(profile);
        ValidateKey(key);
        var existing = Entries.FirstOrDefault(entry => entry.Key == key);
        var value = binding is { IsDefaultOrEmpty: false } bytes ? (ImmutableArray<byte>?)bytes : null;
        var changed = value is { } actual ? document.SetBind(key.Spelling, actual.AsSpan()) : document.RemoveBind(key.Spelling);
        var action = value is { } candidate ? Row(Encoding.Latin1.GetString(candidate.AsSpan()))?.Action : null;
        var conflict = value is { } incoming && existing is not null && !incoming.AsSpan().SequenceEqual(existing.Bytes.AsSpan()) ? existing : null;
        var edit = new ConfigBindingEdit(profile.Profile.Id, action, key, value is null ? null : key,
            conflict, profile.Version, changed);
        CheckLocks(edit, profile);
        return edit;
    }

    public static ConfigHistoryEntry? Apply(ConfigBindingEdit edit, ConfigProfileStore store,
        bool replaceConflict = false, bool runningGame = false)
    {
        if (runningGame)
            throw new ConfigProfileException(ConfigProfileFailure.GameRunning);
        var current = store.Read(edit.ProfileId);
        if (!current.Version.Matches(edit.Before) || current.Document.Dialect != edit.Document.Dialect)
            throw new ConfigProfileException(ConfigProfileFailure.Conflict);
        CheckLocks(edit, current);
        if (edit.Conflict is not null && !replaceConflict)
            throw new ConfigBindingException(ConfigBindingFailure.ConflictNeedsReview);
        return edit.HasChanges ? store.Update(edit.ProfileId, edit.Document, edit.Before, null, edit.Summary) : null;
    }

    private void ValidateSource(ConfigProfileRead profile)
    {
        if (!profile.Version.Config.AsSpan().SequenceEqual(document.Serialized.AsSpan()) || profile.Document.Dialect != document.Dialect)
            throw new ConfigProfileException(ConfigProfileFailure.Conflict);
    }

    private static void ValidateKey(EngineKey? key)
    {
        if (key is { Number: 0 })
            throw new ArgumentException("An assigned binding needs an engine key.", nameof(key));
        if (key is { Reserved: true })
            throw new ConfigBindingException(ConfigBindingFailure.ReservedKey);
    }

    private static void CheckLocks(ConfigBindingEdit edit, ConfigProfileRead profile)
    {
        var keys = new[] { edit.OldKey, edit.Key }.OfType<EngineKey>().Distinct().ToArray();
        var commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (edit.Action is { } action)
            commands.Add(action.Name);
        foreach (var key in keys)
            foreach (var document in new[] { profile.Document, edit.Document })
                if (document.EffectiveBindBytes(key.Spelling) is { } bytes &&
                    new ConfigDocument(bytes.AsSpan(), document.Dialect).Commands.FirstOrDefault() is { Tokens.IsEmpty: false } statement)
                    commands.Add(statement.Tokens[0].Text);

        foreach (string locked in profile.Metadata.Locks.Order(StringComparer.Ordinal))
        {
            string name = locked.ToLowerInvariant();
            if ((name.StartsWith("bind:", StringComparison.Ordinal) && EngineKey.Parse(name[5..]) is { } key && keys.Contains(key)) ||
                commands.Contains(name.StartsWith("action:", StringComparison.Ordinal) ? name[7..] : name))
                throw new ConfigBindingException(ConfigBindingFailure.Locked, locked);
        }
    }

    private static ConfigBindingGroup Group(string name) => name switch
    {
        "+forward" or "+back" or "+moveleft" or "+moveright" or "+leanleft" or "+leanright" or "+gostand" or "+stance" or
        "+left" or "+right" or "+strafe" or "+mlook" or "+lookup" or "+lookdown" or "gocrouch" or "goprone" or
        "togglecrouch" or "toggleprone" => ConfigBindingGroup.Movement,
        "+attack" or "+speed" or "+reload" or "+frag" or "+smoke" or "+melee" or "+holdbreath" or "+binoculars" or
        "toggleads" or "weapnext" or "weapprev" => ConfigBindingGroup.Combat,
        "chatmodepublic" or "chatmodeteam" or "say" or "say_team" or "mp_quickmessage" or "+talk" => ConfigBindingGroup.Communication,
        _ => ConfigBindingGroup.GameUI
    };
}
