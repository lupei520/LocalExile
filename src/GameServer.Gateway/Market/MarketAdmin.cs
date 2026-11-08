using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Setting a listing (Agents/docs/dev/market.md): the public switch and the price note of a premium tab, and a note on one
/// item in it, in the game's own note grammar.
/// </summary>
/// <remarks>
/// Two callers reach it: a signed-in player changing their own tab, and the server owner with the admin token changing any
/// account's. The changes are the same either way, and they are exactly what the client's tab settings window will write
/// once its two messages are captured (the public check box and the note box).
/// </remarks>
static class MarketAdmin
{
    /// <summary>The body of a <c>POST /market/set</c> from the owner's admin token.</summary>
    public static string Write(JsonElement body, out int status)
    {
        status = 200;
        if (!MonitorGrant.Authorized(body, out status, out string refusal)) return Fail(refusal);
        return Apply(body, null, out status);
    }

    /// <summary>Applies the changes. A <paramref name="onlyAccount"/> is the signed-in player: their own account only, and
    /// no token is looked at. Null means the caller has already been authorised for any account.</summary>
    public static string Apply(JsonElement body, string? onlyAccount, out int status)
    {
        status = 200;
        if (body.ValueKind is not JsonValueKind.Object) { status = 400; return Fail("a JSON object is required"); }
        string account = Text(body, "account") ?? onlyAccount ?? "";
        string? league = Text(body, "league");
        if (account.Length == 0) { status = 400; return Fail("account is required"); }
        if (onlyAccount is { } own && !string.Equals(own, account, StringComparison.OrdinalIgnoreCase))
        { status = 403; return Fail("a signed-in player changes only their own stash"); }
        if (!body.TryGetProperty("tab", out var tabValue) || !tabValue.TryGetInt32(out int tab) || tab < 0)
        { status = 400; return Fail("tab is required"); }

        bool wantedPublic = body.TryGetProperty("public", out var publicValue) && publicValue.ValueKind is JsonValueKind.True or JsonValueKind.False;
        bool wantedNote = body.TryGetProperty("note", out var noteValue) && noteValue.ValueKind is JsonValueKind.String or JsonValueKind.Null;
        bool wantedItemNote = body.TryGetProperty("itemNote", out var itemNoteValue) && itemNoteValue.ValueKind is JsonValueKind.String or JsonValueKind.Null;
        if (!wantedPublic && !wantedNote && !wantedItemNote) { status = 400; return Fail("nothing to change: send public, note or itemNote"); }

        AccountStash stash;
        try
        {
            stash = AccountStash.For(AccountStash.Key(account, league), Program.Items);
        }
        catch (Exception e) when (e is InvalidOperationException or JsonException)
        {
            status = 400;
            return Fail($"that stash cannot be read: {e.Message}");
        }

        if (wantedPublic && !stash.SetPublic(tab, publicValue.GetBoolean()))
        { status = 400; return Fail("only a premium tab can be public"); }
        if (wantedNote && !stash.SetNote(tab, wantedItemNote ? null : noteValue.GetString()))
        { status = 400; return Fail($"the note is longer than {AccountStash.MaxNote} characters, or the tab is not premium"); }
        if (wantedItemNote)
        {
            if (!body.TryGetProperty("itemId", out var idValue) || !idValue.TryGetUInt32(out uint itemId))
            { status = 400; return Fail("itemId is required with itemNote"); }
            if (!stash.SetItemNote(tab, itemId, itemNoteValue.GetString()))
            { status = 400; return Fail("no such item in that tab"); }
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["account"] = account,
            ["league"] = league,
            ["tab"] = tab,
            ["tabName"] = stash.NameOf(tab),
            ["public"] = stash.IsPublic(tab),
            ["note"] = stash.TabNote(tab) ?? "",
        }.ToJsonString();
    }

    static string? Text(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() : null;

    static string Fail(string reason) => new JsonObject { ["ok"] = false, ["error"] = reason }.ToJsonString();
}
