using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The monitor's grant (Agents/docs/dev/debugging.md, "The monitor"): currency or equipment put into an account's market
/// tab, for a server owner who hands something to a player without them being in game.
/// </summary>
/// <remarks>
/// Equipment is composed with the same <see cref="ItemCrafter"/> the launcher's crafter and <c>/item_give</c> use, and a
/// stack (currency, fragments, essences) is the base type's own item, which the crafter refuses by design. Both go into
/// the stash through the same <see cref="AccountStash"/> the game does, and a stack fills the piles already there before
/// starting another, up to the tab's own limit per cell. Only the market tab is written to
/// (<see cref="AccountStash.EnsureTab"/> makes it), which keeps a mistake away from the player's own pages. The caller
/// must carry the admin token: <see cref="ServerMonitor"/> refuses a write without one.
/// </remarks>
static class MonitorGrant
{
    /// <summary>Where a grant and a sale put their item: one page of the account's stash, by name.</summary>
    public const string MarketTab = "市场";

    /// <summary>How many cells one request may fill.</summary>
    const int MaxPieces = 20;

    /// <summary>The admin token from <c>Game_MONITOR_ADMIN_TOKEN</c>: without one, no request may write.</summary>
    public static string? Token => Environment.GetEnvironmentVariable("Game_MONITOR_ADMIN_TOKEN") is { Length: > 0 } set ? set : null;

    /// <summary>Whether a write request carries the admin token: every write the page offers goes through this.</summary>
    public static bool Authorized(JsonElement body, out int status, out string error)
    {
        status = 200;
        error = "";
        if (Token is null)
        {
            status = 403;
            error = "writing is off: set Game_MONITOR_ADMIN_TOKEN to a password of your own, then restart the Gateway";
            return false;
        }
        if (body.ValueKind is not JsonValueKind.Object) { status = 400; error = "a JSON object is required"; return false; }
        string? token = body.TryGetProperty("token", out var t) && t.ValueKind is JsonValueKind.String ? t.GetString() : null;
        if (string.IsNullOrEmpty(token)) { status = 403; error = "a token is required: it is Game_MONITOR_ADMIN_TOKEN"; return false; }
        if (!string.Equals(token, Token, StringComparison.Ordinal)) { status = 403; error = "the token does not match"; return false; }
        return true;
    }

    /// <summary>The body of a grant request, answered as JSON.</summary>
    public static string Write(JsonElement body, out int status)
    {
        status = 200;
        if (!Authorized(body, out status, out string refusal)) return Fail(refusal);
        string account = Text(body, "account") ?? "";
        string item = Text(body, "item") ?? "";
        string? league = Text(body, "league");
        int count = body.TryGetProperty("count", out var c) && c.TryGetInt32(out int n) ? n : 1;
        int level = body.TryGetProperty("level", out var v) && v.TryGetInt32(out int lv) ? lv : 1;
        string answer = Grant(account, league, item, count, level, out bool refused);
        if (refused) status = 400;
        return answer;
    }

    /// <summary>Puts <paramref name="count"/> of an item into the account's market tab, as JSON.</summary>
    public static string Grant(string account, string? league, string item, int count, int level, out bool refused)
    {
        refused = true;
        if (string.IsNullOrWhiteSpace(account)) return Fail("account is required");
        if (string.IsNullOrWhiteSpace(item)) return Fail("item is required");
        if (Program.Items.Error is { } data) return Fail(data);

        // A bare name is the common case ("Chaos Orb"); the crafter's own JSON spec is passed through.
        string trimmed = item.TrimStart();
        string baseName = trimmed;
        if (trimmed.StartsWith('{'))
        {
            try
            {
                baseName = JsonNode.Parse(trimmed)?["Base"]?.GetValue<string>() ?? "";
            }
            catch (JsonException e) { return Fail($"not an item: {e.Message}"); }
        }
        if (Program.Items.Find(baseName) is not { } type) return Fail($"No item type \"{baseName}\"");

        // A stack is the base type's own item (currency has no mods to roll); anything else is composed by the crafter.
        Item created;
        ItemCatalog.BaseType? coin = type;
        int pieces = 1;
        if (type.Stackable)
        {
            created = new Item(type.Hash, type.Components, type.VisualIdentity);
        }
        else
        {
            coin = null;
            if (ItemCrafter.Build(Program.Items, Program.Rolls, trimmed.StartsWith('{') ? item : new JsonObject { ["Base"] = item }.ToJsonString(),
                    Math.Max(1, level), Random.Shared, out string? why) is not { } built) return Fail(why ?? "that item could not be composed");
            created = built;
            pieces = Math.Clamp(count, 1, MaxPieces);
        }

        var stash = AccountStash.For(AccountStash.Key(account, league), Program.Items);
        int tab = stash.EnsureTab(MarketTab);
        string tabName = stash.NameOf(tab);
        int per = coin is { } stack ? Math.Max(1, (int)stack.MaxStack) : 1;
        int left = coin is null ? pieces : Math.Max(1, count);
        int asked = left;
        var placed = new JsonArray();
        string? note = null;
        while (left > 0 && placed.Count < MaxPieces)
        {
            int chunk = Math.Min(left, per);
            if (StashPlacement.PutStackOrMerge(stash, tab, created with { StackSize = (ushort)chunk }, out string why) is not { } put)
            {
                if (placed.Count == 0) return Fail($"the \"{tabName}\" tab is full: {why}");
                note = "the tab filled up first";
                break;
            }
            left -= chunk;
            placed.Add(new JsonObject { ["id"] = (long)put.Id, ["x"] = (int)put.X, ["y"] = (int)put.Y, ["moved"] = chunk });
        }
        refused = false;
        return Ok(type, created, account, tab, tabName, asked, placed, note);
    }

    /// <summary>What a grant that succeeded, or partly succeeded, answers.</summary>
    static string Ok(ItemCatalog.BaseType type, Item created, string account, int tab, string tabName, int asked, JsonArray placed, string? note)
    {
        var online = new JsonArray();
        foreach (ClientState conn in Program.InGame.Keys.ToList())
            try
            {
                if (string.Equals(conn.Account ?? Program.CharacterName(conn), account, StringComparison.OrdinalIgnoreCase))
                    online.Add(Program.CharacterName(conn));
            }
            catch (Exception e) when (e is ObjectDisposedException or NullReferenceException or InvalidOperationException) { }
        return new JsonObject
        {
            ["ok"] = true,
            ["account"] = account,
            ["tab"] = tab,
            ["tabName"] = tabName,
            ["item"] = type.Name,
            ["rarity"] = created.Rarity.ToString(),
            ["asked"] = asked,
            ["placed"] = placed,
            ["online"] = online,
            ["note"] = note ?? (online.Count > 0 ? "a character of this account is in game: reopen the tab to see it" : ""),
        }.ToJsonString();
    }

    static string? Text(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() : null;

    static string Fail(string reason) => new JsonObject { ["ok"] = false, ["error"] = reason }.ToJsonString();
}
