using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;

/// <summary>
/// The monitor's data (Agents/docs/dev/debugging.md, "The monitor"): what an account's stash holds, read from the save
/// database through the same <see cref="AccountStash"/> the game uses, so the page and the tools built on it see exactly
/// what a player sees. Read-only: nothing here places, takes or saves an item.
/// </summary>
/// <remarks>
/// A stash is per account and league (<see cref="AccountStash.Key"/>), which is why this reads by account rather than by
/// connection: a market or a gift does not need the player in game. The account's <c>items</c> carry the id the stash
/// gives them (0x40000000 and up), so a listing can name one without the player's own numbering.
/// </remarks>
static class MonitorData
{
    /// <summary>An account's stash in a league as JSON, or null when there is no account name.</summary>
    public static string? Stash(string account, string? league)
    {
        if (string.IsNullOrWhiteSpace(account)) return null;
        string key = AccountStash.Key(account, league);
        AccountStash stash = AccountStash.For(key, Program.Items);

        var tabs = new JsonArray();
        var currency = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        int items = 0;
        for (int tab = 0; tab < stash.Count; tab++)
        {
            IReadOnlyList<InventoryItem> held = stash.Inventory(tab).Items;
            var listed = new JsonArray();
            foreach (InventoryItem item in held)
            {
                JsonObject node = Item(item);
                listed.Add(node);
                items++;
                if (node["class"]?.GetValue<string>() == "StackableCurrency")
                {
                    string name = node["name"]?.GetValue<string>() ?? "?";
                    currency[name] = currency.GetValueOrDefault(name) + node["stack"]!.GetValue<int>();
                }
            }
            if (listed.Count == 0) continue;
            tabs.Add(new JsonObject
            {
                ["tab"] = tab,
                ["name"] = stash.NameOf(tab),
                ["type"] = stash.TypeOf(tab),
                // The market's rule, so the page can say which page may be listed and which is already: only a premium
                // tab can be public (Agents/docs/dev/market.md).
                ["premium"] = stash.TabType(tab) is { } kind and not (0 or 16),
                ["public"] = stash.IsPublic(tab),
                ["note"] = stash.TabNote(tab) ?? "",
                ["items"] = listed,
            });
        }

        var totals = new JsonObject();
        foreach ((string name, long count) in currency.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal))
            totals[name] = count;

        return new JsonObject
        {
            ["account"] = account,
            ["league"] = league,
            ["key"] = key,
            ["gold"] = stash.Gold,
            ["tabCount"] = stash.Count,
            ["itemCount"] = items,
            ["currency"] = totals,
            ["tabs"] = tabs,
        }.ToJsonString();
    }

    /// <summary>The stat descriptions the client shows, loaded once (1.9 MB of templates: "<c>{0}% increased Life</c>").</summary>
    static readonly Lazy<StatDescriptions> Descriptions = new(() => StatDescriptions.Load());

    /// <summary>
    /// An item as the game displays it (Agents/docs/dev/market.md): the name line, the base line, the mods in the client's
    /// own order, then the item's own facts. The order and the grouping are the game's, not ours — the wire carries each
    /// mod's section (enchant, implicit, explicit, …) and the templates come from the client's stat descriptions.
    /// </summary>
    public static JsonArray Card(Item item, ItemCatalog.BaseType type)
    {
        var card = new JsonArray();
        string name = item.NameWords.Count > 0 ? Program.Rolls.NameOf(item.NameWords) + " " + type.Name : type.Name;
        card.Add(Line("name", name));
        card.Add(Line("base", type.Name == name ? type.Class : $"{type.Name} · {type.Class}"));

        foreach (ModSection section in new[] { ModSection.Enchant, ModSection.Implicit, ModSection.Explicit })
        {
            var mods = item.Mods.Where(mod => mod.Section == section).ToList();
            if (mods.Count == 0) continue;
            // A fractured mod is kept by every other currency: the client draws it apart, so the card says so.
            for (int i = 0; i < mods.Count; i++)
            {
                JsonObject line = Line(section switch
                {
                    ModSection.Enchant => "enchant",
                    ModSection.Implicit => "implicit",
                    _ => i < item.FracturedCount ? "fractured" : "explicit",
                }, ModText(mods[i]));
                line["section"] = Section(section);
                if (Tier(mods[i], type) is int tier and > 0) line["tier"] = tier;
                card.Add(line);
            }
        }

        var facts = new JsonArray();
        facts.Add($"物品等级 {item.ItemLevel}");
        if (item.Quality > 0) facts.Add($"品质 +{item.Quality}%");
        if (item.Sockets.Count > 0) facts.Add($"{item.Sockets.Count} 孔" + (item.Links is { Count: > 0 } links ? $"（{string.Join('-', links)} 连）" : ""));
        if (item.StackSize > 1) facts.Add($"堆叠 {item.StackSize}");
        if (!item.Identified) facts.Add("未鉴定");
        string states = ItemStates.Describe(item);
        if (states != "No item states") facts.Add(states);
        if (facts.Count > 0) card.Add(Line("facts", string.Join(" · ", facts)));
        return card;
    }

    /// <summary>One mod's text: the client's own sentence with the rolled values filled in, or the raw stat ids when the
    /// game data has no template for it.</summary>
    static string ModText(ItemMod mod)
    {
        var rolled = new Dictionary<string, int>(StringComparer.Ordinal);
        if (Program.Items.Mods.ByHash(mod.Hash) is { } row)
            for (int i = 0; i < row.Stats.Count; i++)
                if (i < mod.Values.Count) rolled[row.Stats[i].Id] = mod.Values[i];
        if (rolled.Count > 0)
            foreach (string stat in rolled.Keys)
                if (Descriptions.Value.For(stat) is { } description && Pick(description) is { } variant)
                {
                    // The template names a value either by the description's own stats ("{0}", "{1}") or by a handler
                    // (a name and the stat it takes, written 1-based in the client's file).
                    var named = new Dictionary<string, int>(StringComparer.Ordinal);
                    for (int i = 0; i < description.Stats.Count; i++)
                        if (rolled.TryGetValue(description.Stats[i], out int value))
                        {
                            named[i.ToString()] = value;
                            named[description.Stats[i]] = value;
                        }
                    foreach ((string name, int index) in variant.Handlers)
                        foreach (int slot in new[] { index - 1, index })
                        {
                            if (slot < 0 || slot >= description.Stats.Count || !rolled.TryGetValue(description.Stats[slot], out int value)) continue;
                            named[name] = value;
                            break;
                        }
                    return Fill(variant.Text, named);
                }
        return Program.Items.Mods.Describe(mod);
    }

    /// <summary>The template with every <c>{name}</c> filled, honouring the format the client writes after a colon
    /// (<c>{0:+d}</c> keeps the sign, <c>{0:.1f}</c> is a tenth: the game stores such stats ten times over).</summary>
    static string Fill(string template, Dictionary<string, int> named) =>
        System.Text.RegularExpressions.Regex.Replace(template, @"\{([^}:]+)(?::([^}]*))?\}", match =>
            named.TryGetValue(match.Groups[1].Value, out int value) ? Format(value, match.Groups[2].Success ? match.Groups[2].Value : null) : match.Value);

    /// <summary>A rolled value as the client prints it: signed when the template asks for it, scaled when it has decimals.</summary>
    static string Format(int value, string? format)
    {
        if (string.IsNullOrEmpty(format)) return value.ToString();
        bool signed = format.StartsWith('+');
        int decimals = format.Contains('.') ? format.Length - format.IndexOf('.') - 2 : 0;
        if (decimals > 0)
        {
            double scaled = value / Math.Pow(10, decimals);
            return (signed && scaled > 0 ? "+" : "") + scaled.ToString("0." + new string('0', decimals));
        }
        return (signed && value > 0 ? "+" : "") + value;
    }

    /// <summary>Where a mod's roll sits among the tiers this base can roll (1 is the highest), or 0 when the game data
    /// does not group it. An affix's tiers are the mods carrying the same stats, generation type and domain, and only the
    /// ones that can spawn on the base count — which is why a pair of boots shows T1 where another base shows T6.</summary>
    public static int Tier(ItemMod mod, ItemCatalog.BaseType type) =>
        Tiers.Value.TryGetValue((mod.Hash, type.Row), out int tier) ? tier : 0;

    /// <summary>The tier of each (mod, base) pair the mod can roll on, built once and kept: a few seconds' work over every
    /// base and mod the game has, and then a lookup per affix on a card.</summary>
    static readonly Lazy<Dictionary<(ushort Hash, int Row), int>> Tiers = new(BuildTiers);

    static Dictionary<(ushort, int), int> BuildTiers()
    {
        // Which mods name a tag in one of their spawn weights: a base's candidate affixes are those of its own tags.
        var byTag = new Dictionary<string, List<ModCatalog.Mod>>(StringComparer.Ordinal);
        var keyOf = new Dictionary<ushort, string>();
        foreach (ModCatalog.Mod mod in Program.Items.Mods.All)
        {
            keyOf[mod.Hash] = $"{mod.GenerationType}|{mod.Domain}|{string.Join(",", mod.Families)}|{string.Join(",", mod.Stats.Select(stat => stat.Id))}";
            foreach ((string tag, int weight) in mod.SpawnWeights)
                if (weight > 0)
                {
                    if (!byTag.TryGetValue(tag, out var list)) byTag[tag] = list = [];
                    list.Add(mod);
                }
        }
        var tiers = new Dictionary<(ushort, int), int>();
        foreach (ItemCatalog.BaseType type in Program.Items.All)
        {
            if (type.Tags.Count == 0) continue;
            var groups = new Dictionary<string, List<ModCatalog.Mod>>(StringComparer.Ordinal);
            var seen = new HashSet<ushort>();
            foreach (string tag in type.Tags)
                foreach (ModCatalog.Mod mod in byTag.GetValueOrDefault(tag) ?? [])
                    if (seen.Add(mod.Hash))
                    {
                        string key = keyOf[mod.Hash];
                        if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
                        list.Add(mod);
                    }
            foreach (var (_, mods) in groups)
            {
                if (mods.Count < 2) continue;
                List<int> levels = [.. mods.Select(mod => mod.Level).Distinct().OrderByDescending(level => level)];
                foreach (ModCatalog.Mod mod in mods) tiers[(mod.Hash, type.Row)] = levels.IndexOf(mod.Level) + 1;
            }
        }
        return tiers;
    }

    /// <summary>The texts the page can suggest for an affix search: the client's own lines with their placeholders
    /// removed, so what is offered is exactly what the search matches ("to maximum Life", "% increased Fire Resistance").</summary>
    public static string StatSuggestions(string query)
    {
        var texts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        string wanted = query.Trim();
        if (wanted.Length >= 2)
            foreach (StatDescriptions.Description description in Descriptions.Value.All)
            {
                foreach (StatDescriptions.Variant variant in description.Variants)
                {
                    string text = Plain(variant.Text);
                    if (text.Length >= 3 && text.Contains(wanted, StringComparison.OrdinalIgnoreCase)) texts.Add(text);
                }
                if (texts.Count > 400) break;
            }
        var offered = new JsonArray();
        foreach (string text in texts.OrderBy(text => text.Length).Take(20)) offered.Add(text);
        return new JsonObject { ["stats"] = offered }.ToJsonString();
    }

    /// <summary>A template without its placeholders, and the search text of one affix.</summary>
    static string Plain(string text) =>
        string.Join(' ', System.Text.RegularExpressions.Regex.Replace(text, @"\{[^}]*\}", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>What the client's templates say for a stat, all variants (empty when the game data has none): the text a
    /// search matches against, so "maximum Life" finds every item with a life affix however it was rolled.</summary>
    public static string StatTexts(string statId) => Descriptions.Value.For(statId) is { } description
        ? string.Join(" | ", description.Variants.Select(variant => variant.Text)) : "";

    /// <summary>The variant the client shows by default: the one without conditions, else the first.</summary>
    static StatDescriptions.Variant? Pick(StatDescriptions.Description description) =>
        description.Variants.FirstOrDefault(v => v.Conditions.All(c => c == "#")) ?? description.Variants.FirstOrDefault();

    static JsonObject Line(string kind, string text) => new() { ["kind"] = kind, ["text"] = text };

    static string Section(ModSection section) => section switch
    {
        ModSection.Enchant => "enchant",
        ModSection.Implicit => "implicit",
        ModSection.Explicit => "explicit",
        _ => section.ToString().ToLowerInvariant(),
    };

    /// <summary>The accounts and their characters, from the save database: what the page's picker offers.</summary>
    public static string Accounts()
    {
        var store = SqliteAccountStore.Open();
        var accounts = new JsonArray();
        foreach (var account in store.Accounts().OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            var characters = new JsonArray();
            foreach (var character in store.CharactersOf(account.Id).OrderByDescending(c => c.LastPlayedUtc))
                characters.Add(new JsonObject
                {
                    ["name"] = character.Name,
                    ["class"] = character.Class.ToString(),
                    ["level"] = character.Level,
                    ["league"] = character.League,
                });
            accounts.Add(new JsonObject { ["name"] = account.Name, ["characters"] = characters });
        }
        return new JsonObject { ["accounts"] = accounts }.ToJsonString();
    }

    /// <summary>One item: what it is, how many, and the id the stash knows it by.</summary>
    static JsonObject Item(InventoryItem held)
    {
        Item item = held.Item;
        var type = Program.Items.ByHash(item.TypeHash);
        return new JsonObject
        {
            ["id"] = (long)held.Id,
            ["x"] = (int)held.X,
            ["y"] = (int)held.Y,
            ["name"] = type?.Name ?? "?",
            ["type"] = type?.Id ?? "",
            ["class"] = type?.Class ?? "",
            ["rarity"] = item.Rarity.ToString(),
            ["ilvl"] = (int)item.ItemLevel,
            ["quality"] = (int)item.Quality,
            ["stack"] = (int)item.StackSize,
            ["mods"] = item.Mods.Count,
            ["corrupted"] = item.Corrupted,
        };
    }
}
