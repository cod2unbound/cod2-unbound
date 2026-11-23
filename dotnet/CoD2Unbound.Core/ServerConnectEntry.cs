namespace CoD2Unbound.Core;

/// <summary>Parses a typed endpoint or join link without losing the explicitly requested edition.</summary>
public sealed record ServerConnectEntry(string? Address, ServerAddress? Target, GameEdition? Edition,
    bool InvalidAddress, string? PasswordProblem)
{
    public static ServerConnectEntry Parse(string input, string password)
    {
        string text = input.Trim();
        var link = ServerLink.Parse(text);
        bool hasScheme = text.StartsWith("cod2://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("cod2x://", StringComparison.OrdinalIgnoreCase);
        var endpoint = hasScheme ? link?.Target : ServerAddress.Parse(text);
        if (password.Length == 0)
            password = endpoint?.Password ?? "";
        string? problem = ServerAddress.PasswordProblem(password);
        var target = endpoint is null || problem is not null ? null :
            endpoint with { Password = password.Length == 0 ? null : password };
        return new(endpoint?.Address, target, link?.Edition, text.Length != 0 && endpoint is null, problem);
    }

    public static async Task<GameServer?> AskAsync(ServerQueryClient query, string address, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken).ConfigureAwait(false);
        return await query.ServerAsync(address, cancellationToken).ConfigureAwait(false);
    }
}
