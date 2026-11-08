using System.Collections.Concurrent;
using System.Security.Cryptography;
using GameServer.Core.Accounts;
using GameServer.Storage;

/// <summary>
/// Who is at the page (Agents/docs/dev/market.md): a player signs in with the same account and password the game uses,
/// and the token that comes back is what lets them list their own tabs and buy. The admin token is a separate thing: it
/// is the server owner's, and it is the only one that may grant items or look at another account.
/// </summary>
/// <remarks>
/// The check is the game's own (<see cref="AccountService.Login"/>), so a password changed in the game changes here too,
/// and a disabled account cannot sign in. Tokens live in this process only: restarting the Gateway signs everyone out.
/// </remarks>
static class MarketSession
{
    sealed record Login(string Account, DateTime ExpiresUtc);

    static readonly ConcurrentDictionary<string, Login> Tokens = new(StringComparer.Ordinal);
    static readonly TimeSpan Life = TimeSpan.FromHours(12);

    /// <summary>Signs an account in, or says why not.</summary>
    public static string? SignIn(string account, string password, out string token, out string error)
    {
        token = "";
        error = "";
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrEmpty(password))
        {
            error = "account and password are required";
            return null;
        }
        Result<GameServer.Core.Accounts.Session> result;
        try
        {
            result = new AccountService(SqliteAccountStore.Open()).Login(account, password);
        }
        catch (Exception e) when (e is InvalidOperationException or System.Text.Json.JsonException)
        {
            error = $"the account database cannot be read: {e.Message}";
            return null;
        }
        if (!result.Ok || result.Value is not { } session)
        {
            error = result.Error == AuthError.BadCredentials ? "wrong account or password" : result.Error.ToString();
            return null;
        }
        string name = account.Trim();
        foreach ((string old, Login gone) in Tokens.Where(pair => pair.Value.ExpiresUtc <= DateTime.UtcNow).ToList())
            Tokens.TryRemove(old, out _);
        token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Tokens[token] = new Login(name, DateTime.UtcNow + Life);
        return name;
    }

    /// <summary>The account a token belongs to, or null when it is unknown or expired.</summary>
    public static string? AccountOf(string? token)
    {
        if (string.IsNullOrEmpty(token) || !Tokens.TryGetValue(token, out Login? login)) return null;
        if (login.ExpiresUtc <= DateTime.UtcNow)
        {
            Tokens.TryRemove(token, out _);
            return null;
        }
        return login.Account;
    }

    /// <summary>Forgets a token (the page's sign out).</summary>
    public static void SignOut(string? token)
    {
        if (!string.IsNullOrEmpty(token)) Tokens.TryRemove(token, out _);
    }

    /// <summary>The bearer token of a request's headers, or a session=… in its query string.</summary>
    public static string? TokenOf(string? header, string? session)
    {
        if (header is { Length: > 7 } && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return header[7..].Trim();
        return string.IsNullOrEmpty(session) ? null : session;
    }
}
