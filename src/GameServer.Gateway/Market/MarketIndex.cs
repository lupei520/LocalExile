using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;

/// <summary>
/// The market index (Agents/docs/dev/market.md): every listing the server would publish, built from the stashes
/// themselves — a public premium tab, a price note, and the item sitting in it. Nothing is copied out and nothing is
/// reserved, exactly as the official trade site only indexes a tab the player made public.
/// </summary>
/// <remarks>
/// A listing is read from the save database rather than from a connection, so a seller who is offline is still listed,
/// which is the official behaviour. The price of an item is its own note if it has one and the tab's note otherwise; an
/// item with neither is not listed. Filtering happens here so that the page can stay a thin view of one JSON object.
/// </remarks>
static class MarketIndex
{
    /// <summary>What the page asks for: any of these may be empty. <paramref name="Stats"/> is the trade site's mod
    /// search as text: <c>maximum Life&gt;=60; Fire Resistance&gt;=30</c> — each entry needs one of the item's affixes
    /// whose template says that, rolled at least that high.</summary>
    public sealed record Filter(string? Name = null, string? Class = null, string? Rarity = null, string? Currency = null,
        long? Min = null, long? Max = null, bool OnlineOnly = false, string? Stats = null);

    /// <summary>The price a listing is at: the item's own note when it has one, and the tab's otherwise. This is the one
    /// reading of a listing's price — the index and the buy both go through it, so they cannot disagree.</summary>
    public static MarketNote.Price? PriceOf(AccountStash stash, int tab, InventoryItem held) =>
        MarketNote.Parse(stash.ItemNote(tab, held.Id) ?? stash.TabNote(tab));

    /// <summary>Every listing the stashes publish, as JSON, with the filters applied.</summary>
    public static string Listings(Filter filter)
    {
        var wanted = Wanted(filter.Stats);
        var listings = new JsonArray();
        var currencies = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var classes = new SortedSet<string>(StringComparer.Ordinal);
        var rarities = new SortedSet<string>(StringComparer.Ordinal);
        int skipped = 0;

        foreach ((string account, string? league, AccountStash stash) in Stashes())
        {
            bool online = IsOnline(account, out string? character);
            for (int tab = 0; tab < stash.Count; tab++)
            {
                if (!stash.IsPublic(tab)) continue;
                string note = stash.TabNote(tab) ?? "";
                foreach (InventoryItem held in stash.Inventory(tab).Items)
                {
                    string? itemNote = stash.ItemNote(tab, held.Id);
                    if (PriceOf(stash, tab, held) is not { } price)
                    {
                        skipped++;
                        continue;
                    }
                    if (Program.Items.ByHash(held.Item.TypeHash) is not { } type)
                    {
                        skipped++;
                        continue;
                    }
                    // The pickers the page offers are built from everything listed, so choosing one cannot empty them.
                    if (type.Class.Length > 0) classes.Add(type.Class);
                    rarities.Add(held.Item.Rarity.ToString());
                    if (price.Currency.Length > 0) currencies.Add(price.Currency);
                    if (filter.Name is { Length: > 0 } name && !type.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (filter.Class is { Length: > 0 } wantedClass && !string.Equals(type.Class, wantedClass, StringComparison.OrdinalIgnoreCase)) continue;
                    if (filter.Rarity is { Length: > 0 } wantedRarity && !string.Equals(held.Item.Rarity.ToString(), wantedRarity, StringComparison.OrdinalIgnoreCase)) continue;
                    if (filter.Currency is { Length: > 0 } wantedCurrency && !string.Equals(price.Currency, wantedCurrency, StringComparison.OrdinalIgnoreCase)) continue;
                    if (filter.Min is { } min && price.Amount < min) continue;
                    if (filter.Max is { } max && price.Amount > max) continue;
                    if (filter.OnlineOnly && !online) continue;
                    if (wanted.Count > 0 && !HasStats(held.Item, wanted)) continue;

                    listings.Add(new JsonObject
                    {
                        ["seller"] = account,
                        ["character"] = character ?? "",
                        ["online"] = online,
                        ["league"] = league ?? StashLeagueOf(account, league),
                        ["tab"] = tab,
                        ["tabName"] = stash.NameOf(tab),
                        ["id"] = (long)held.Id,
                        ["name"] = type.Name,
                        ["type"] = type.Id,
                        ["class"] = type.Class,
                        ["rarity"] = held.Item.Rarity.ToString(),
                        ["ilvl"] = (int)held.Item.ItemLevel,
                        ["quality"] = (int)held.Item.Quality,
                        ["stack"] = (int)held.Item.StackSize,
                        ["mods"] = held.Item.Mods.Count,
                        ["corrupted"] = held.Item.Corrupted,
                        ["card"] = MonitorData.Card(held.Item, type),
                        ["spoken"] = itemNote is not null,
                        ["note"] = itemNote ?? note,
                        ["price"] = new JsonObject
                        {
                            ["kind"] = price.Kind,
                            ["amount"] = price.Amount,
                            ["currency"] = price.Currency,
                            ["give"] = price.Give,
                            ["giveCurrency"] = price.GiveCurrency,
                            ["text"] = price.Text,
                        },
                        ["whisper"] = Whisper(account, type.Name, price, StashLeagueOf(account, league)),
                    });
                }
            }
        }
        return new JsonObject
        {
            ["count"] = listings.Count,
            ["unlisted"] = skipped,
            ["currencies"] = new JsonArray([.. currencies.Select(currency => (JsonNode)currency)]),
            ["classes"] = new JsonArray([.. classes.Select(itemClass => (JsonNode)itemClass)]),
            ["rarities"] = new JsonArray([.. rarities.Select(rarity => (JsonNode)rarity)]),
            ["listings"] = listings,
        }.ToJsonString();
    }

    /// <summary>Every stash a player could have listed: each account's own (Standard) and one per league its characters
    /// are in, since a stash and its listings are per league.</summary>
    public static IEnumerable<(string Account, string? League, AccountStash Stash)> Stashes()
    {
        var store = SqliteAccountStore.Open();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in store.Accounts().OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            var leagues = new List<string?> { null };
            leagues.AddRange(store.CharactersOf(account.Id).Select(character => character.League).Distinct().Cast<string?>());
            foreach (string? league in leagues)
            {
                string key = AccountStash.Key(account.Name, league);
                if (!seen.Add(key)) continue;
                AccountStash stash;
                try
                {
                    stash = AccountStash.For(key, Program.Items);
                }
                catch (Exception e) when (e is InvalidOperationException or System.Text.Json.JsonException)
                {
                    // A save this build cannot read (UnknownSavedItemException among them): that account is left out.
                    continue;
                }
                yield return (account.Name, league, stash);
            }
        }
    }

    /// <summary>The wanted affixes, each a piece of the client's text and the least it may roll.</summary>
    static List<(string Text, long Min)> Wanted(string? query)
    {
        var wanted = new List<(string, long)>();
        foreach (string part in (query ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int at = part.IndexOf(">=", StringComparison.Ordinal);
            string text = at < 0 ? part : part[..at].Trim();
            long min = at >= 0 && long.TryParse(part[(at + 2)..].Trim(), out long value) ? value : long.MinValue;
            if (text.Length >= 2) wanted.Add((text, min));
        }
        return wanted;
    }

    /// <summary>Whether the item has an affix whose template says every wanted text, rolled at least as high.</summary>
    static bool HasStats(Item item, List<(string Text, long Min)> wanted)
    {
        foreach ((string text, long min) in wanted)
        {
            bool found = false;
            foreach (ItemMod mod in item.Mods)
            {
                if (Program.Items.Mods.ByHash(mod.Hash) is not { } row) continue;
                for (int i = 0; i < row.Stats.Count && !found; i++)
                {
                    if (!MonitorData.StatTexts(row.Stats[i].Id).Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
                    if (i < mod.Values.Count && mod.Values[i] >= min) found = true;
                }
                if (found) break;
            }
            if (!found) return false;
        }
        return true;
    }

    /// <summary>The league name a stash key stands for ("Standard" for the account's own).</summary>
    static string StashLeagueOf(string account, string? league) => string.IsNullOrEmpty(league) ? "Standard" : league;

    /// <summary>Whether an account has a character in game, and its name.</summary>
    static bool IsOnline(string account, out string? character)
    {
        character = null;
        foreach (ClientState conn in Program.InGame.Keys.ToList())
        {
            try
            {
                if (!string.Equals(conn.Account ?? Program.CharacterName(conn), account, StringComparison.OrdinalIgnoreCase)) continue;
                character = Program.CharacterName(conn);
                return true;
            }
            catch (Exception e) when (e is ObjectDisposedException or NullReferenceException or InvalidOperationException) { }
        }
        return false;
    }

    /// <summary>The whisper the trade site's button copies, in the game's own words.</summary>
    static string Whisper(string seller, string item, MarketNote.Price price, string league) => price.Kind switch
    {
        "offer" => $"@{seller} Hi, I'd like to make an offer on your {item} in {league}",
        "free" => $"@{seller} Hi, could I have your {item} in {league}?",
        "bulk" => $"@{seller} Hi, I'd like to buy your {price.Give} {price.GiveCurrency} for my {price.Amount} {price.Currency} in {league}",
        _ => $"@{seller} Hi, I'd like to buy your {item} listed for {price.Amount} {price.Currency} in {league}",
    };
}
