using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The monitor's item forge (Agents/docs/dev/debugging.md, "The monitor"): an admin composes an item in the browser —
/// a craftable base, its rarity and item level, and affixes chosen from that base's own pools — and it lands in the
/// account's market tab, exactly the gift <see cref="MonitorGrant"/> makes.
/// </summary>
/// <remarks>
/// The base and affix data come from the same <see cref="ItemCrafter.Catalog"/> the desktop crafter mode prints, so the
/// page and the launcher agree on what may be rolled. An affix is refused unless it is in the pool of that base at the
/// requested item level (domain, prefix or suffix, level, spawn weights, influence tags — <see cref="ItemRolls.Pool"/>),
/// which keeps the page's freedom at vanilla rules: it is a way to roll a choice of real affixes, not to put any mod on
/// anything. Prefix/suffix caps follow the rare roll: three and three (two and two on jewels). The item is built with
/// <see cref="ItemCrafter.Build"/> and placed with the same stash path <see cref="MonitorGrant"/> uses, so a forge and a
/// grant behave the same once composed. Every write still carries the admin token via <see cref="MonitorGrant.Authorized"/>.
/// </remarks>
static class MonitorForge
{
    const string JsonType = "application/json; charset=utf-8";

    /// <summary>The prefix and suffix caps a composed item may fill, as the rare roll has them (ItemRolls.cs).</summary>
    const int MaxPrefix = 3, MaxSuffix = 3, MaxPrefixJewel = 2, MaxSuffixJewel = 2;

    static readonly Lazy<StatDescriptions> Descriptions = new(() => StatDescriptions.Load());

    /// <summary>The forge page's data, or null when it is not a forge route.</summary>
    public static (string Body, string Type, int Status)? Answer(string path, string method, JsonElement? body)
    {
        (string Body, string Type, int Status)? plain(string text, int status) => (text, "text/plain; charset=utf-8", status);
        switch (path)
        {
            case "/forge/catalog" when method == "GET":
                return (ForgeCatalog(), JsonType, 200);
            case "/forge" when method == "POST":
                return body is { } asked ? Forge(asked) : plain("a JSON body is required", 400);
            case "/forge" when method != "POST":
                return plain("POST a JSON body: {token, account, base, ...}", 405);
            case "/forge/preview" when method == "POST":
                return body is { } preview ? Preview(preview) : plain("a JSON body is required", 400);
            case "/forge/preview":
                return plain("POST a JSON body: {token, base, ...}", 405);
            default:
                return null;
        }
    }

    /// <summary>The bases and affixes the forge offers, as JSON: what <see cref="ItemCrafter.Catalog"/> prints the
    /// desktop crafter, joined into one document the page keeps in memory.</summary>
    public static string ForgeCatalog()
    {
        if (Program.Items.Error is { } data) return new JsonObject { ["ok"] = false, ["error"] = data }.ToJsonString();
        var classes = new JsonArray();
        var pools = new JsonArray();
        var mods = new JsonArray();
        var bases = new JsonArray();
        foreach (var line in ItemCrafter.Catalog(Program.Items, Program.Rolls, Program.Bench, Descriptions.Value))
            switch (line["type"]?.GetValue<string>())
            {
                case "class": classes.Add(line["id"]!.DeepClone() is { } id && line["name"] is { } name
                    ? new JsonObject { ["id"] = id, ["name"] = name } : null); break;
                case "pool": pools.Add(new JsonObject
                {
                    ["id"] = line["id"]!.DeepClone(),
                    ["mods"] = line["mods"]!.DeepClone(),
                }); break;
                case "mod":
                    mods.Add(new JsonObject
                    {
                        ["id"] = line["id"]!.DeepClone(),
                        ["name"] = line["name"]!.DeepClone(),
                        ["generation"] = line["generation"]!.DeepClone(),
                        ["level"] = line["level"]!.DeepClone(),
                        ["stats"] = line["stats"]!.DeepClone(),
                    });
                    break;
                case "base":
                    bases.Add(new JsonObject
                    {
                        ["id"] = line["id"]!.DeepClone(),
                        ["name"] = line["name"]!.DeepClone(),
                        ["class"] = line["class"]!.DeepClone(),
                        ["width"] = line["width"]!.DeepClone(),
                        ["height"] = line["height"]!.DeepClone(),
                        ["dropLevel"] = line["dropLevel"]!.DeepClone(),
                        ["requirements"] = line["requirements"]!.DeepClone(),
                        ["sockets"] = line["sockets"]!.DeepClone(),
                        ["quality"] = line["quality"]!.DeepClone(),
                        ["implicits"] = line["implicits"]!.DeepClone(),
                        ["pool"] = line["pool"]!.DeepClone(),
                        ["influence"] = line["influence"]!.DeepClone(),
                        ["crafted"] = line["crafted"]!.DeepClone(),
                        ["corrupted"] = line["corrupted"]!.DeepClone(),
                        ["eldritch"] = line["eldritch"]!.DeepClone(),
                    });
                    break;
            }
        return new JsonObject
        {
            ["ok"] = true,
            ["classes"] = classes,
            ["pools"] = pools,
            ["mods"] = mods,
            ["bases"] = bases,
        }.ToJsonString();
    }

    /// <summary>A compose request, answered as JSON.</summary>
    public static (string Body, string Type, int Status) Forge(JsonElement body)
    {
        if (!MonitorGrant.Authorized(body, out int status, out string refusal))
            return (new JsonObject { ["ok"] = false, ["error"] = refusal }.ToJsonString(), JsonType, status);
        if (!TryCompose(body, out string spec, out int level, out string account, out string? league, out string error))
            return (new JsonObject { ["ok"] = false, ["error"] = error }.ToJsonString(), JsonType, 400);
        string answer = MonitorGrant.Grant(account, league, spec, 1, level, out bool refused);
        return refused
            ? (answer, JsonType, 400)
            : (answer, JsonType, 200);
    }

    /// <summary>The same composition without placing it: what the item's card would say, so the page can show the
    /// item before the admin spends the grant.</summary>
    public static (string Body, string Type, int Status) Preview(JsonElement body)
    {
        if (!MonitorGrant.Authorized(body, out int status, out string refusal))
            return (new JsonObject { ["ok"] = false, ["error"] = refusal }.ToJsonString(), JsonType, status);
        if (!TryCompose(body, out string spec, out int level, out _, out _, out string error))
            return (new JsonObject { ["ok"] = false, ["error"] = error }.ToJsonString(), JsonType, 400);
        if (ItemCrafter.Build(Program.Items, Program.Rolls, spec, level, Random.Shared, out string? why) is not { } item)
            return (new JsonObject { ["ok"] = false, ["error"] = why ?? "the item could not be composed" }.ToJsonString(), JsonType, 400);
        if (Program.Items.ByHash(item.TypeHash) is not { } type)
            return (new JsonObject { ["ok"] = false, ["error"] = "the base is not in the catalog" }.ToJsonString(), JsonType, 400);
        return (new JsonObject
        {
            ["ok"] = true,
            ["card"] = MonitorData.Card(item, type),
        }.ToJsonString(), JsonType, 200);
    }

    /// <summary>Reads the request, checks the base and every affix against the vanilla rules, and writes the spec
    /// <see cref="ItemCrafter.Build"/> takes (/item_give's JSON).</summary>
    static bool TryCompose(JsonElement body, out string spec, out int level, out string account, out string? league, out string error)
    {
        spec = ""; league = null; account = ""; level = 1;
        error = "";
        if (body.ValueKind is not JsonValueKind.Object) { error = "a JSON object is required"; return false; }

        account = String(body, "account");
        league = body.TryGetProperty("league", out var lg) && lg.ValueKind is JsonValueKind.String ? lg.GetString() : null;
        if (string.IsNullOrWhiteSpace(account)) { error = "account is required"; return false; }

        string baseId = String(body, "base");
        if (string.IsNullOrWhiteSpace(baseId)) { error = "base is required"; return false; }
        if (Program.Items.Find(baseId) is not { } type) { error = $"No item type \"{baseId}\""; return false; }
        if (!ItemCrafter.Craftable(Program.Items, type)) { error = $"{type.Name} is not a craftable item"; return false; }

        string rarity = body.TryGetProperty("rarity", out var r) && r.ValueKind is JsonValueKind.String ? r.GetString()!.Trim() : "rare";
        if (!Enum.TryParse<ItemRarity>(rarity, true, out ItemRarity rarityValue) || !Enum.IsDefined(rarityValue))
        { error = $"no rarity {rarity}"; return false; }

        level = body.TryGetProperty("itemLevel", out var il) && il.TryGetInt32(out int wanted)
            ? Math.Clamp(wanted, 1, 100) : Math.Max(1, type.DropLevel);

        // The affixes the base can have at this item level: the rule drops and currency roll with, influence mods
        // behind their own tags.
        var pool = Program.Rolls.Pool(type, level).Select(p => p.Mod).ToList();
        var byId = pool.ToDictionary(m => m.Id, StringComparer.Ordinal);
        // The rare roll's own caps: two and two on jewels, three and three elsewhere (ItemRolls.cs).
        bool jewel = type.Class.Contains("Jewel", StringComparison.Ordinal);
        int capPrefix = jewel ? MaxPrefixJewel : MaxPrefix;
        int capSuffix = jewel ? MaxSuffixJewel : MaxSuffix;
        int prefixes = 0, suffixes = 0;
        var chosen = new JsonArray();

        // Implicits stay the base's own: the forge does not rewrite them, and the crafter rolls them itself.
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
            if (mod.GenerationType == ItemRolls.Prefix)
            {
                if (++prefixes > capPrefix) { error = $"at most {capPrefix} prefixes"; return false; }
            }
            else if (mod.GenerationType == ItemRolls.Suffix)
            {
                if (++suffixes > capSuffix) { error = $"at most {capSuffix} suffixes"; return false; }
            }
            else
            {
                error = $"\"{mod.Name}\" is not a prefix or a suffix"; return false;
            }
            if (chosen.Any(n => n?["id"]?.GetValue<string>() == id)) { error = $"\"{mod.Name}\" is on the item twice"; return false; }
            var line = new JsonObject { ["id"] = id };
            if (element.TryGetProperty("values", out var values) && values.ValueKind is JsonValueKind.Array)
            {
                var rolled = new JsonArray();
                foreach (var value in values.EnumerateArray())
                    if (value.TryGetInt32(out int v)) rolled.Add(v);
                line["values"] = rolled;
            }
            chosen.Add(line);
        }

        if (rarityValue is ItemRarity.Magic && prefixes + suffixes > 2)
        { error = "a magic item has at most 2 affixes"; return false; }
        if (rarityValue is ItemRarity.Rare && prefixes + suffixes is 0)
        { error = "a rare without affixes: pick rarity normal instead"; return false; }
        if (rarityValue is ItemRarity.Normal && prefixes + suffixes > 0)
        { error = "a normal item has no affixes: pick magic, rare or unique instead"; return false; }

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
        foreach (string state in Enumerate(body, "states").Select(e => e.ToString()).Where(s => s.Length > 0))
        {
            specNode.TryGetPropertyValue("States", out var states);
            if (states is not JsonArray list) specNode["States"] = list = [];
            list.Add(state);
        }
        spec = specNode.ToJsonString();
        return true;
    }

    /// <summary>A JSON body's array of objects under <paramref name="name"/>, or an empty list.</summary>
    static IEnumerable<JsonElement> Enumerate(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Array
            ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();

    /// <summary>A JSON object's string field, or "".</summary>
    static string String(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() ?? "" : "";
}
