using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// The monitor's world and loot tuning (the "调参" section): an admin reads the tunable system settings
/// (<see cref="SystemSettings"/>) and writes new values, which take effect at once through the same path the
/// <c>/game_data setting</c> command uses (<see cref="GameDataStore.SetSetting"/> + <see cref="GameData.Reload"/>) —
/// no restart. The tunable keys are the world/loot ones: drop multipliers per kind, monster pack density and size,
/// the rare/magic pack chances and the rare-pack floor, item quantity and experience.
/// </summary>
/// <remarks>
/// GET /tune lists the keys with their current value and seed default; POST /tune writes one key after
/// <see cref="SystemSettings.Validate"/> rejects anything out of range, and POST /tune/reset restores one key or all
/// of them to their seeds. Every write carries the admin token (<see cref="MonitorGrant.Authorized"/>); reads stay
/// open like the other monitor reads.
/// </remarks>
static class MonitorTune
{
    const string JsonType = "application/json; charset=utf-8";

    /// <summary>The keys the page tunes, grouped, with a label each. Order is the page's order.</summary>
    public static readonly (string Group, (string Key, string Label)[] Keys)[] Groups =
    [
        ("掉落",
        [
            ("item_quantity_multiplier", "全局物品数量"),
            ("drop_multiplier_currency", "通货掉落"),
            ("drop_multiplier_equipment", "装备掉落"),
            ("drop_multiplier_flasks", "药剂掉落"),
            ("drop_multiplier_gems", "宝石掉落"),
            ("drop_multiplier_maps", "地图掉落"),
            ("drop_multiplier_unique_rarity", "物品稀有度（unique 概率）"),
        ]),
        ("怪物群",
        [
            ("monster_pack_density_multiplier", "怪物群密度"),
            ("monster_pack_size_multiplier", "怪物群大小"),
            ("monster_rare_pack_chance", "稀有怪群概率（%）"),
            ("monster_magic_pack_chance", "魔法怪群概率（%）"),
            ("monster_min_rare_packs", "每图最少稀有群"),
        ]),
        ("经验与金币",
        [
            ("experience_multiplier", "经验倍率"),
            ("gold_drop_multiplier", "金币掉落"),
            ("gold_drop_chance_multiplier", "金币掉落概率"),
        ]),
    ];

    /// <summary>Every tunable key flat, for the write path.</summary>
    static readonly HashSet<string> Keys = [.. Groups.SelectMany(g => g.Keys.Select(k => k.Key))];

    /// <summary>One tune route's answer, or null when the path is not the tuner's.</summary>
    public static (string Body, string Type, int Status)? Answer(string path, string method, JsonElement? body)
    {
        switch (path)
        {
            case "/tune" when method == "GET":
                return (List(), JsonType, 200);
            case "/tune" when method == "POST":
                if (body is not { } asked) return Fail("a JSON body is required", 400);
                return Write(asked);
            case "/tune/reset" when method == "POST":
                if (body is not { } reset) return Fail("a JSON body is required", 400);
                return Reset(reset);
            case "/tune" or "/tune/reset":
                return Fail("GET /tune, POST /tune {key, value}, POST /tune/reset {key?}", 405);
            default:
                return null;
        }
    }

    static string List()
    {
        var root = new JsonObject { ["ok"] = true, ["groups"] = new JsonArray() };
        foreach (var (group, keys) in Groups)
        {
            var node = new JsonObject { ["group"] = group, ["keys"] = new JsonArray() };
            foreach (var (key, label) in keys)
                ((JsonArray)node["keys"]!).Add(new JsonObject
                {
                    ["key"] = key,
                    ["label"] = label,
                    ["value"] = SystemSettings.Default.Get(key),
                    ["default"] = SystemSettings.Seeds.GetValueOrDefault(key),
                });
            ((JsonArray)root["groups"]!).Add(node);
        }
        return root.ToJsonString();
    }

    static (string Body, string Type, int Status) Fail(string error, int status) =>
        (new JsonObject { ["ok"] = false, ["error"] = error }.ToJsonString(), JsonType, status);

    static (string Body, string Type, int Status) Write(JsonElement body)
    {
        if (!MonitorGrant.Authorized(body, out int status, out string refusal))
            return Fail(refusal, status);
        string key = Text(body, "key");
        if (!Keys.Contains(key))
            return Fail($"\"{key}\" is not a tunable setting", 400);
        if (!body.TryGetProperty("value", out var value) || value.ValueKind is not JsonValueKind.Number
            || !value.TryGetDouble(out double number))
            return Fail("a numeric value is required", 400);
        if (!SystemSettings.Validate(key, number, out string? error))
            return Fail(error!, 400);

        var store = GameData.Store;
        if (store is null)
            return Fail("the game-data store is not open", 500);
        try
        {
            store.SetSetting(key, number);
            GameData.Reload("monitor tune");
            return (new JsonObject
            {
                ["ok"] = true,
                ["key"] = key,
                ["value"] = SystemSettings.Default.Get(key),
            }.ToJsonString(), JsonType, 200);
        }
        catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException or ArgumentException)
        {
            return Fail($"the write failed: {e.Message}", 500);
        }
    }

    static (string Body, string Type, int Status) Reset(JsonElement body)
    {
        if (!MonitorGrant.Authorized(body, out int status, out string refusal))
            return Fail(refusal, status);
        var store = GameData.Store;
        if (store is null)
            return Fail("the game-data store is not open", 500);
        // No key: every tunable one. With one: just that, refused when it is not tunable.
        List<string> targets = body.TryGetProperty("key", out var k) && k.ValueKind is JsonValueKind.String
            ? [k.GetString()!] : [.. Keys];
        try
        {
            var restored = new JsonArray();
            foreach (string target in targets)
            {
                if (!Keys.Contains(target))
                    return Fail($"\"{target}\" is not a tunable setting", 400);
                store.DeleteSetting(target);
                restored.Add(target);
            }
            GameData.Reload("monitor tune reset");
            return (new JsonObject { ["ok"] = true, ["restored"] = restored }.ToJsonString(), JsonType, 200);
        }
        catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException or ArgumentException)
        {
            return Fail($"the reset failed: {e.Message}", 500);
        }
    }

    static string Text(JsonElement body, string name) =>
        body.TryGetProperty(name, out var t) && t.ValueKind is JsonValueKind.String ? t.GetString() ?? "" : "";
}
