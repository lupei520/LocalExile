using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class MonitorForgeTests
{
    // The forge's rules are the game's own: an affix has to be in the base's pool at that item level, one roll per affix
    // line (the life tiers carry no family at all, so the family rule alone would let two of them on), and the base's
    // implicit travels with the item — ItemCrafter.Build composes exactly the mods it is given.
    [Fact]
    public void The_forge_keeps_the_games_affix_rules()
    {
        string? before = Environment.GetEnvironmentVariable("Game_MONITOR_ADMIN_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("Game_MONITOR_ADMIN_TOKEN", "a-test-token");
            var ring = Program.Items.All.First(type => type.Name == "Iron Ring");
            var life = Program.Rolls.Pool(ring, 84).Select(pair => pair.Mod)
                .Where(mod => mod.GenerationType == ItemRolls.Prefix && mod.Stats.Any(stat => stat.Id == "base_maximum_life"))
                .OrderByDescending(mod => mod.Level).ToList();
            Assert.True(life.Count >= 2, "the ring rolls more than one tier of maximum life");

            (string Body, string Type, int Status) Preview(JsonObject body)
            {
                using var document = JsonDocument.Parse(body.ToJsonString());
                return MonitorForge.Preview(document.RootElement);
            }
            JsonObject Body(params JsonObject[] mods) => new()
            {
                ["token"] = "a-test-token",
                ["base"] = ring.Id,
                ["rarity"] = "rare",
                ["itemLevel"] = 84,
                ["mods"] = new JsonArray([.. mods.Cast<JsonNode>()]),
            };
            JsonObject Mod(string id) => new() { ["id"] = id };

            var (answer, _, status) = Preview(Body(Mod(life[0].Id)));
            Assert.Equal(200, status);
            var card = JsonNode.Parse(answer)!;
            Assert.True((bool)card["ok"]!);
            Assert.Contains(card["card"]!.AsArray(), line => (string?)line!["kind"] == "implicit");

            var (twice, _, twiceStatus) = Preview(Body(Mod(life[0].Id), Mod(life[1].Id)));
            Assert.Equal(400, twiceStatus);
            Assert.Contains("another tier", (string)JsonNode.Parse(twice)!["error"]!);

            var (outside, _, outsideStatus) = Preview(Body(Mod(ring.Id)));
            Assert.Equal(400, outsideStatus);
            Assert.Contains("is not an affix", (string)JsonNode.Parse(outside)!["error"]!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Game_MONITOR_ADMIN_TOKEN", before);
        }
    }
}
