using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The monitor's item forge (Agents/docs/dev/debugging.md, "The monitor"): the crafting simulator at <c>/forge</c>, where
/// an admin picks a craftable base, rolls real affixes into the item slot and hands the item to an account — it lands in
/// that account's market tab, exactly the gift <see cref="MonitorGrant"/> makes.
/// </summary>
/// <remarks>
/// The bases and the affix pools come from the same game data the desktop crafter mode prints (<see cref="ItemCrafter"/>),
/// the wording from the client's own stat descriptions (<see cref="MonitorData.RangeText"/>), and every affix is listed
/// with its roll range and the tier that base rolls it at (<see cref="MonitorData.Tier">the market card's own
/// grouping</see>). The page asks one base at a time (<c>/forge/base</c>) instead of pulling the whole catalog, which is
/// what makes it open on a normal machine in a moment.
/// <para>
/// An affix is refused unless it is in the pool of that base at the requested item level (domain, prefix or suffix, level,
/// spawn weights, influence tags — <see cref="ItemRolls.Pool"/>) or the bench's own modifier of that class, and prefixes
/// and suffixes are capped as the rolls cap them: three and three, two and two on jewels, one and one on a magic item.
/// Two affixes of one family cannot share an item, as in the game. The item is composed with
/// <see cref="ItemCrafter.Build"/> and placed through the stash path <see cref="MonitorGrant"/> uses, so a forged item and
/// a granted one behave the same. Every write carries the admin token (<see cref="MonitorGrant.Authorized"/>).
/// </para>
/// </remarks>
static class MonitorForge
{
    const string JsonType = "application/json; charset=utf-8";

    /// <summary>The prefix and suffix caps a composed item may fill, as the rare roll has them (ItemRolls.cs).</summary>
    const int MaxPrefix = 3, MaxSuffix = 3, MaxPrefixJewel = 2, MaxSuffixJewel = 2;

    /// <summary>The tier of every affix of a base (1 the highest), built on that base's first request.</summary>
    static readonly ConcurrentDictionary<int, IReadOnlyDictionary<ushort, int>> Tiers = new();

    static readonly Lazy<Dictionary<string, string>> ClassNames = new(() => ItemCrafter.ClassNames());

    /// <summary>One forge route's answer, or null when the path is not the forge's. The simulator itself is GET /forge.</summary>
    public static (string Body, string Type, int Status)? Answer(string path, string method, string query, JsonElement? body)
    {
        (string Body, string Type, int Status)? plain(string text, int status) => (text, "text/plain; charset=utf-8", status);
        switch (path)
        {
            case "/forge" when method == "GET":
                return (MonitorForgePage.Html, "text/html; charset=utf-8", 200);
            case "/forge/list" when method == "GET":
                return (BaseList(), JsonType, 200);
            case "/forge/base" when method == "GET":
                return (BaseAffixes(ServerMonitor.Query(query, "base") ?? "", Int(ServerMonitor.Query(query, "ilvl"))), JsonType, 200);
            // The item's own picture (Monitoring/ItemArt.cs), decoded offline from the client's art.
            case "/forge/art" when method == "GET":
                return (ItemArt.Answer(ServerMonitor.Query(query, "base") ?? ""), JsonType, 200);
            case "/forge/preview" when method == "POST":
                return body is { } preview ? Preview(preview) : plain("a JSON body is required", 400);
            case "/forge" when method == "POST":
                return body is { } asked ? Forge(asked) : plain("a JSON body is required", 400);
            case "/forge" or "/forge/list" or "/forge/base" or "/forge/art" or "/forge/preview":
                return plain("GET /forge (the page), GET /forge/list, GET /forge/base?base=&ilvl=, GET /forge/art?base=, POST /forge/preview, POST /forge", 405);
            default:
                return null;
        }
    }

    /// <summary>The bases a page may offer, with the classes they belong to: the craftable items, by class and name.</summary>
    public static string BaseList()
    {
        if (Program.Items.Error is { } error) return Fail(error);
        var craftable = Program.Items.All.Where(type => ItemCrafter.Craftable(Program.Items, type))
            .OrderBy(type => type.Class, StringComparer.Ordinal).ThenBy(type => type.Name, StringComparer.Ordinal).ToList();
        var classes = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var bases = new JsonArray();
        foreach (ItemCatalog.BaseType type in craftable)
        {
            if (seen.Add(type.Class)) classes.Add(new JsonObject { ["id"] = type.Class, ["name"] = ClassName(type.Class) });
            var (str, dex, @int) = Program.Items.Requirements(type);
            bases.Add(new JsonObject
            {
                ["id"] = type.Id,
                ["name"] = type.Name,
                ["class"] = type.Class,
                ["dropLevel"] = type.DropLevel,
                ["requirements"] = new JsonArray(str, dex, @int),
                ["width"] = type.Width,
                ["height"] = type.Height,
            });
        }
        return new JsonObject { ["ok"] = true, ["classes"] = classes, ["bases"] = bases }.ToJsonString();
    }

    /// <summary>One base's own implicits and every affix it may roll at an item level, with ranges and tiers: what the
    /// simulator's two side panels are built from.</summary>
    public static string BaseAffixes(string baseId, int itemLevel)
    {
        if (Program.Items.Error is { } error) return Fail(error);
        if (string.IsNullOrWhiteSpace(baseId)) return Fail("base is required");
        if (Program.Items.Find(baseId) is not { } type) return Fail($"No item type \"{baseId}\"");
        if (!ItemCrafter.Craftable(Program.Items, type)) return Fail($"{type.Name} is not a craftable item");
        itemLevel = Math.Clamp(itemLevel <= 0 ? 100 : itemLevel, 1, 100);
        IReadOnlyDictionary<ushort, int> tiers = Tiers.GetOrAdd(type.Row, _ => BaseTiers(type));

        // The base's affixes and the bench's own modifier of this class; a mod kept once, whatever lists it in.
        var prefixes = new List<JsonObject>();
        var suffixes = new List<JsonObject>();
        foreach (var (mod, _) in Program.Rolls.Pool(type, itemLevel))
        {
            if (mod.GenerationType is not (ItemRolls.Prefix or ItemRolls.Suffix)) continue;
            (mod.GenerationType == ItemRolls.Prefix ? prefixes : suffixes).Add(Affix(mod, tiers));
        }
        var crafted = new List<JsonObject>();
        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (BenchCrafting.Recipe recipe in Program.Bench.All)
            if (recipe.Mod is { Domain: 9 } mod && recipe.Classes.Contains(type.ClassRow) && kept.Add(mod.Id))
                crafted.Add(Affix(mod, tiers));

        var implicits = new JsonArray();
        foreach (ModCatalog.Mod mod in type.Implicits ?? [])
            implicits.Add(new JsonObject
            {
                ["id"] = mod.Id,
                ["name"] = mod.Name,
                ["text"] = MonitorData.RangeText(mod),
                ["stats"] = Ranges(mod),
            });

        bool jewel = Jewel(type);
        var (str, dex, @int) = Program.Items.Requirements(type);
        return new JsonObject
        {
            ["ok"] = true,
            ["base"] = new JsonObject
            {
                ["id"] = type.Id,
                ["name"] = type.Name,
                ["class"] = type.Class,
                ["className"] = ClassName(type.Class),
                ["itemLevel"] = itemLevel,
                ["dropLevel"] = type.DropLevel,
                ["requirements"] = new JsonArray(str, dex, @int),
                ["sockets"] = CurrencyEffects.SocketCapacity(ItemCatalog.Create(type), type),
                ["quality"] = type.Components.Contains("Quality"),
                ["width"] = type.Width,
                ["height"] = type.Height,
            },
            // The caps of the two rarities that take affixes (a normal item takes none): the rare roll's own, and a
            // magic item's one and one.
            ["caps"] = new JsonObject
            {
                ["rare"] = new JsonObject { ["prefix"] = jewel ? MaxPrefixJewel : MaxPrefix, ["suffix"] = jewel ? MaxSuffixJewel : MaxSuffix },
                ["magic"] = new JsonObject { ["prefix"] = 1, ["suffix"] = 1 },
            },
            ["implicits"] = implicits,
            ["prefixes"] = Sorted(prefixes),
            ["suffixes"] = Sorted(suffixes),
            ["crafted"] = Sorted(crafted),
        }.ToJsonString();
    }

    /// <summary>A compose request, answered as JSON.</summary>
    public static (string Body, string Type, int Status) Forge(JsonElement body)
    {
        if (!MonitorGrant.Authorized(body, out int status, out string refusal))
            return (new JsonObject { ["ok"] = false, ["error"] = refusal }.ToJsonString(), JsonType, status);
        if (!TryCompose(body, true, out string spec, out int level, out string account, out string? league, out string error))
            return (new JsonObject { ["ok"] = false, ["error"] = error }.ToJsonString(), JsonType, 400);
        string answer = MonitorGrant.Grant(account, league, spec, 1, level, out bool refused);
        return (answer, JsonType, refused ? 400 : 200);
    }

    /// <summary>The same composition without placing it: what the item's card would say, so the simulator can show the
    /// item — its rolled name and values — before the admin spends the grant.</summary>
    public static (string Body, string Type, int Status) Preview(JsonElement body)
    {
        if (!MonitorGrant.Authorized(body, out int status, out string refusal))
            return (new JsonObject { ["ok"] = false, ["error"] = refusal }.ToJsonString(), JsonType, status);
        if (!TryCompose(body, false, out string spec, out int level, out _, out _, out string error))
            return (new JsonObject { ["ok"] = false, ["error"] = error }.ToJsonString(), JsonType, 400);
        if (ItemCrafter.Build(Program.Items, Program.Rolls, spec, level, Random.Shared, out string? why) is not { } item)
            return (new JsonObject { ["ok"] = false, ["error"] = why ?? "the item could not be composed" }.ToJsonString(), JsonType, 400);
        if (Program.Items.ByHash(item.TypeHash) is not { } type)
            return (new JsonObject { ["ok"] = false, ["error"] = "the base is not in the catalog" }.ToJsonString(), JsonType, 400);
        return (new JsonObject { ["ok"] = true, ["card"] = MonitorData.Card(item, type) }.ToJsonString(), JsonType, 200);
    }

    /// <summary>Reads the request, checks the base and every affix against the vanilla rules, and writes the spec
    /// <see cref="ItemCrafter.Build"/> takes (/item_give's JSON). <paramref name="needAccount"/> is a grant's rule: the
    /// preview composes without one.</summary>
    static bool TryCompose(JsonElement body, bool needAccount, out string spec, out int level, out string account, out string? league, out string error)
    {
        spec = ""; league = null; account = ""; level = 1;
        error = "";
        if (body.ValueKind is not JsonValueKind.Object) { error = "a JSON object is required"; return false; }

        account = String(body, "account");
        league = body.TryGetProperty("league", out var lg) && lg.ValueKind is JsonValueKind.String ? lg.GetString() : null;
        if (needAccount && string.IsNullOrWhiteSpace(account)) { error = "account is required"; return false; }

        string baseId = String(body, "base");
        if (string.IsNullOrWhiteSpace(baseId)) { error = "base is required"; return false; }
        if (Program.Items.Find(baseId) is not { } type) { error = $"No item type \"{baseId}\""; return false; }
        if (!ItemCrafter.Craftable(Program.Items, type)) { error = $"{type.Name} is not a craftable item"; return false; }

        string rarity = body.TryGetProperty("rarity", out var r) && r.ValueKind is JsonValueKind.String ? r.GetString()!.Trim() : "rare";
        if (!Enum.TryParse<ItemRarity>(rarity, true, out ItemRarity rarityValue) || !Enum.IsDefined(rarityValue))
        { error = $"no rarity {rarity}"; return false; }

        level = body.TryGetProperty("itemLevel", out var il) && il.TryGetInt32(out int wanted)
            ? Math.Clamp(wanted, 1, 100) : Math.Max(1, type.DropLevel);

        // What the base can roll at this item level, plus the bench's own modifier of its class (domain 9).
        var byId = Program.Rolls.Pool(type, level).Select(pair => pair.Mod).ToDictionary(mod => mod.Id, StringComparer.Ordinal);
        foreach (BenchCrafting.Recipe recipe in Program.Bench.All)
            if (recipe.Mod is { Domain: 9 } bench && recipe.Classes.Contains(type.ClassRow)) byId.TryAdd(bench.Id, bench);

        // The caps of the item's rarity, as the roll has them.
        var (capPrefix, capSuffix) = rarityValue switch
        {
            ItemRarity.Magic => (1, 1),
            ItemRarity.Rare => Jewel(type) ? (MaxPrefixJewel, MaxSuffixJewel) : (MaxPrefix, MaxSuffix),
            _ => (0, 0),
        };
        int prefixes = 0, suffixes = 0;
        var families = new HashSet<int>();
        var lines = new HashSet<string>(StringComparer.Ordinal);
        // The base's own implicits are part of the item: ItemCrafter.Build composes exactly the mods it is given, so they
        // are named first, their values left to the crafter to roll.
        var chosen = new JsonArray();
        foreach (ModCatalog.Mod implicitMod in type.Implicits ?? [])
            chosen.Add(new JsonObject { ["id"] = implicitMod.Id, ["section"] = "implicit" });

        foreach (var element in Enumerate(body, "mods"))
        {
            if (element.ValueKind is not JsonValueKind.Object) { error = "every mod is an object {id, values?}"; return false; }
            string id = String(element, "id");
            if (string.IsNullOrWhiteSpace(id)) { error = "a mod without an id"; return false; }
            if (!byId.TryGetValue(id, out var mod))
            {
                error = $"\"{id}\" is not an affix this base can roll at item level {level}";
                return false;
            }
            if (mod.GenerationType is not (ItemRolls.Prefix or ItemRolls.Suffix))
            {
                error = $"\"{mod.Name}\" is not a prefix or a suffix";
                return false;
            }
            if (chosen.Any(node => node?["id"]?.GetValue<string>() == id))
            {
                error = $"\"{mod.Name}\" is on the item twice";
                return false;
            }
            // One affix per family, and one roll per affix line: the game never puts two rolls of one stat on an item,
            // and the life tiers carry no family at all, so both rules are needed.
            if (mod.Families.Where(family => family > 0).Any(family => !families.Add(family)))
            {
                error = $"\"{mod.Name}\" shares its affix group with one already on the item";
                return false;
            }
            if (!lines.Add(LineKey(mod)))
            {
                error = $"\"{mod.Name}\" is another tier of an affix already on the item";
                return false;
            }
            if (mod.GenerationType == ItemRolls.Prefix)
            {
                if (++prefixes > capPrefix) { error = capPrefix == 0 ? "this rarity takes no affixes" : $"at most {capPrefix} prefixes"; return false; }
            }
            else if (++suffixes > capSuffix)
            {
                error = capSuffix == 0 ? "this rarity takes no affixes" : $"at most {capSuffix} suffixes";
                return false;
            }
            var line = new JsonObject { ["id"] = id };
            if (element.TryGetProperty("values", out var values) && values.ValueKind is JsonValueKind.Array)
            {
                var rolled = new JsonArray();
                foreach (var value in values.EnumerateArray())
                    if (value.TryGetInt32(out int v)) rolled.Add(v);
                if (rolled.Count > 0) line["values"] = rolled;
            }
            chosen.Add(line);
        }

        if (rarityValue is ItemRarity.Rare && prefixes + suffixes is 0)
        { error = "a rare without affixes: pick rarity normal instead"; return false; }

        var specNode = new JsonObject
        {
            ["Base"] = type.Name,
            ["Rarity"] = rarityValue.ToString(),
            ["ItemLevel"] = level,
            ["Identified"] = true,
        };
        if (body.TryGetProperty("quality", out var q) && q.TryGetInt32(out int quality) && quality != 0)
            specNode["Quality"] = Math.Clamp(quality, 0, 100);
        if (body.TryGetProperty("sockets", out var sc) && sc.ValueKind is JsonValueKind.String)
            specNode["Sockets"] = sc.GetString();
        if (chosen.Count > 0) specNode["Mods"] = chosen;
        foreach (string state in Enumerate(body, "states").Select(node => node.ToString()).Where(state => state.Length > 0))
        {
            specNode.TryGetPropertyValue("States", out var states);
            if (states is not JsonArray list) specNode["States"] = list = [];
            list.Add(state);
        }
        spec = specNode.ToJsonString();
        return true;
    }

    /// <summary>One affix as the page lists it: what it says (with its ranges), the item level it needs, the tier this
    /// base rolls it at, and its family and affix line — the page greys out what would conflict with the item.</summary>
    static JsonObject Affix(ModCatalog.Mod mod, IReadOnlyDictionary<ushort, int> tiers) => new()
    {
        ["id"] = mod.Id,
        ["name"] = mod.Name,
        ["generation"] = mod.GenerationType,
        ["level"] = mod.Level,
        ["tier"] = tiers.GetValueOrDefault(mod.Hash),
        ["families"] = new JsonArray([.. mod.Families.Where(family => family > 0).Select(family => (JsonNode)JsonValue.Create(family))]),
        ["group"] = LineKey(mod),
        ["text"] = MonitorData.RangeText(mod),
        ["stats"] = Ranges(mod),
    };

    /// <summary>What makes two mods the same affix line: the same kind of roll on the same kind of item over the same
    /// stats, which is how the tiers of one affix are grouped (the market card's own grouping, MonitorData.Tier).</summary>
    static string LineKey(ModCatalog.Mod mod) =>
        $"{mod.GenerationType}|{mod.Domain}|{string.Join(",", mod.Stats.Select(stat => stat.Id))}";

    /// <summary>A mod's stats with the roll range each has (an admin's own range override included).</summary>
    static JsonArray Ranges(ModCatalog.Mod mod)
    {
        var stats = new JsonArray();
        for (int i = 0; i < mod.Stats.Count; i++)
        {
            var (min, max) = mod.RangeAt(i);
            stats.Add(new JsonObject { ["id"] = mod.Stats[i].Id, ["min"] = min, ["max"] = max });
        }
        return stats;
    }

    /// <summary>Affixes are listed by their sentence with the values blanked, so the tiers of one stat sit together
    /// (strongest first), which is how a crafting tool is read.</summary>
    static string Template(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"[-+]?[0-9]+(\.[0-9]+)?", "#");

    static JsonArray Sorted(List<JsonObject> affixes)
    {
        affixes.Sort((a, b) =>
        {
            int byTemplate = string.CompareOrdinal(Template(a["text"]!.GetValue<string>()), Template(b["text"]!.GetValue<string>()));
            if (byTemplate != 0) return byTemplate;
            int tierA = a["tier"] is { } ta && ta.GetValue<int>() > 0 ? ta.GetValue<int>() : int.MaxValue;
            int tierB = b["tier"] is { } tb && tb.GetValue<int>() > 0 ? tb.GetValue<int>() : int.MaxValue;
            return tierA != tierB ? tierA.CompareTo(tierB) : a["level"]!.GetValue<int>().CompareTo(b["level"]!.GetValue<int>());
        });
        return new JsonArray([.. affixes]);
    }

    /// <summary>The tier of every affix of one base, grouped as the market card groups them: the same generation, domain,
    /// families and stat ids, ranked by the item levels this base's tags allow. <see cref="MonitorData.Tier"/> builds the
    /// same table for every base at once; the forge wants one base and keeps what it built.</summary>
    static IReadOnlyDictionary<ushort, int> BaseTiers(ItemCatalog.BaseType type)
    {
        var groups = new Dictionary<string, List<ModCatalog.Mod>>(StringComparer.Ordinal);
        foreach (ModCatalog.Mod mod in Program.Items.Mods.All)
        {
            bool spawns = false;
            foreach ((string tag, int weight) in mod.SpawnWeights)
                if (weight > 0 && type.Tags.Contains(tag)) { spawns = true; break; }
            if (!spawns) continue;
            string key = $"{mod.GenerationType}|{mod.Domain}|{string.Join(",", mod.Families)}|{string.Join(",", mod.Stats.Select(stat => stat.Id))}";
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(mod);
        }
        var tiers = new Dictionary<ushort, int>();
        foreach (List<ModCatalog.Mod> group in groups.Values)
        {
            if (group.Count < 2) continue;
            List<int> levels = [.. group.Select(mod => mod.Level).Distinct().OrderByDescending(level => level)];
            foreach (ModCatalog.Mod mod in group) tiers[mod.Hash] = levels.IndexOf(mod.Level) + 1;
        }
        return tiers;
    }

    /// <summary>The class a base shows: ItemClasses.json's name, or its own id when the file has none.</summary>
    static string ClassName(string id) => ClassNames.Value.GetValueOrDefault(id, id);

    /// <summary>Jewels cap their affixes at two and two (ItemRolls.cs).</summary>
    static bool Jewel(ItemCatalog.BaseType type) => type.Class.Contains("Jewel", StringComparison.Ordinal);

    /// <summary>An int out of a query value, or 0.</summary>
    static int Int(string? value) => int.TryParse(value, out int parsed) ? parsed : 0;

    /// <summary>A JSON body's array of objects under <paramref name="name"/>, or an empty list.</summary>
    static IEnumerable<JsonElement> Enumerate(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Array
            ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();

    /// <summary>A JSON object's string field, or "".</summary>
    static string String(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() ?? "" : "";

    static string Fail(string reason) => new JsonObject { ["ok"] = false, ["error"] = reason }.ToJsonString();
}
