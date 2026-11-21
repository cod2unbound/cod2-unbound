using System.Collections.Immutable;

namespace CoD2Unbound.Core;

public sealed record ConfigProfileTrashHandle(string Token);

public interface IConfigProfileTrash
{
    // Publish to a recoverable Trash and return its identity. Report a failed move only if it did not publish.
    ConfigProfileTrashHandle Move(string path);
    // Restore the exact item without replacing an existing profile.
    void Restore(ConfigProfileTrashHandle handle, string target);
}

public sealed partial class ConfigProfileStore
{
    private const string TrashedChain = "/.studio-advanced-binds.cfg";

    public bool RestoreConnectionSelection(ImmutableArray<byte> original, ImmutableArray<byte> expectedActive)
    {
        using var lease = files.Acquire(default);
        CheckWritable();
        if (!SameBytes(Selection().OriginalBytes, expectedActive))
            return false;
        var tokens = ConfigDocument.Tokenize(original.AsSpan(), ConfigDialect.Stock).Tokens;
        if (tokens.IsEmpty)
            throw new ConfigProfileException(ConfigProfileFailure.InvalidIdentifier);

        try { ReadLocked(tokens[0].Text); }
        catch (ConfigProfileException error) when (error.Failure == ConfigProfileFailure.MissingProfile) { return false; }
        Write(Active, original);
        return true;
    }

    public void DiscardConnectionProfile(string id, string sourceProfileId, ImmutableArray<byte> expectedActive)
    {
        if (id.Length != 38 || !id.StartsWith("csconn", StringComparison.Ordinal) ||
            !id[6..].All(char.IsAsciiHexDigitLower))
            throw new ConfigProfileException(ConfigProfileFailure.InvalidIdentifier);

        using var lease = files.Acquire(default);
        CheckWritable();
        string path = DirectoryName(id);
        if (!files.DirectoryExists(path))
            return;
        var profile = ReadLocked(id);
        if (profile.Metadata.Origin.Kind != ConfigOriginKind.Copied || profile.Metadata.Origin.Source != sourceProfileId)
            throw Conflict();
        var selected = Selection();
        if (id.Equals(selected.ProfileId, StringComparison.OrdinalIgnoreCase))
        {
            if (!SameBytes(selected.OriginalBytes, expectedActive))
                throw Conflict();
            Delete(Active);
        }
        if (profile.Version.Chain is not null)
            Delete(ChainPath(id));
        CheckWritable();
        files.DeleteOwnedDirectory(path);
    }

    public ConfigProfileTrashHandle MoveToTrash(string id, ConfigProfileVersion expected, IConfigProfileTrash trash)
    {
        using var lease = files.Acquire(default);
        CheckWritable();
        string directory = DirectoryName(id);
        var profile = ReadLocked(id);
        if (!profile.Version.Matches(expected))
            throw Conflict();
        if (id.Equals(Selection().ProfileId, StringComparison.OrdinalIgnoreCase))
            throw new ConfigProfileException(ConfigProfileFailure.InUse);

        string chainPath = ChainPath(id);
        string stash = directory + TrashedChain;
        if (profile.Version.Chain is { } chain)
        {
            Write(stash, chain);
            Delete(chainPath);
        }
        CheckWritable();
        try { return trash.Move(directory); }
        catch (IOException)
        {
            try { RestoreTrashedChain(id, directory); }
            catch (IOException) { /* Keep the stash for the next recovery. */ }
            throw new ConfigProfileException(ConfigProfileFailure.CannotTrash);
        }
    }

    public void RestoreFromTrash(ConfigProfileTrashHandle handle, string id, IConfigProfileTrash trash)
    {
        using var lease = files.Acquire(default);
        CheckWritable();
        string path = DirectoryName(id);
        if (files.DirectoryExists(path) || files.ListDirectories(Players).Contains(id, StringComparer.OrdinalIgnoreCase))
            throw new ConfigProfileException(ConfigProfileFailure.AlreadyExists);
        trash.Restore(handle, path);
        RestoreTrashedChain(id, path);
    }

    private void RestoreTrashedChain(string id, string directory)
    {
        string stash = directory + TrashedChain;
        if (files.Read(stash) is not { } saved || gameIsRunning?.Invoke() == true)
            return;
        string chain = ChainPath(id);
        if (files.Read(chain) is { } current)
        {
            if (!SameBytes(current, saved))
                throw Conflict();
        }
        else
        {
            Write(chain, saved);
        }
        Delete(stash);
    }
}
