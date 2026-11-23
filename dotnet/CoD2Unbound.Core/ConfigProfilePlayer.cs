namespace CoD2Unbound.Core;

public sealed partial class ConfigProfileStore
{
    public void SavePlayerName(string id, string name)
    {
        name = name.Trim();
        if (SetupPlayer.NameProblem(name) is { } problem)
            throw new ArgumentException(problem);
        var current = Read(id);
        string value = name.Length == 0 ? SetupPlayer.Unknown : name;
        if (current.Document.EffectiveSetting("name") == value)
            return;
        Update(id, current.Document.SetSetting("name", value), current.Version, null, "Changed player name");
    }
}
