using System.Text.Json;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>
/// A refused use as the combat lab's own evidence (Modules/Core/Game/Combat/DamageLedger.cs: /damage's summary in
/// Modules/Core/Player/Commands/Rules.CombatLab.cs, Agents/docs/dev/combat-lab.md). A use the ordinary skill path
/// turns down before any hit used to leave the ledger empty, so "0 records" read the same whether a skill never fired
/// or a damage formula was never reached. These pin the two things a script depends on: a refusal is the existing
/// record type with Kind "refused" and the reason in outcome, and the summary counts it apart from hits.
/// </summary>
public sealed class CombatLabRefusalTests
{
    [Fact]
    public void A_refusal_serialises_as_the_existing_record_with_kind_refused_and_no_damage()
    {
        var line = DamageLedger.Json(Refusal());

        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        Assert.Equal("refused", root.GetProperty("kind").GetString());
        Assert.Equal("SplittingSteel", root.GetProperty("skill").GetString());   // the effect id the use carried
        Assert.Equal(0x0f3fu, root.GetProperty("skillId").GetUInt16());
        Assert.Equal("no available projectile recipe/metadata", root.GetProperty("outcome").GetString());
        Assert.Equal(0, root.GetProperty("raw").GetInt32());
        Assert.Equal(0, root.GetProperty("taken").GetInt32());
        Assert.Equal("", root.GetProperty("types").GetString());
        Assert.False(root.GetProperty("crit").GetBoolean());
        // The aim is 0/""/0/0 when the use had none, so a refusal is never mistaken for a hit on a target.
        Assert.Equal(0u, root.GetProperty("targetId").GetUInt32());
        Assert.Equal("", root.GetProperty("target").GetString());
        Assert.Equal(0u, root.GetProperty("lifeBefore").GetUInt32());
        Assert.Equal(0u, root.GetProperty("lifeAfter").GetUInt32());
        Assert.Equal(0, root.GetProperty("distance").GetDouble());
    }

    [Fact]
    public void The_damage_summary_names_refusals_separately_from_hits()
    {
        // One hit, one critical hit, one refusal: a refused use adds no damage and is not a hit.
        var path = "logs/damage-OfflineWitch.jsonl";
        Assert.Equal("damage 3 record(s) -> logs/damage-OfflineWitch.jsonl (1 hit, 1 refused, 1 critical, 80 taken)",
            CommandsRules.DamageSummary([Hit(raw: 400, taken: 50), Hit(raw: 900, taken: 30, critical: true), Refusal()], path));
        Assert.Equal("damage 1 record(s) -> logs/damage-OfflineWitch.jsonl (0 hit, 1 refused, 0 critical, 0 taken)",
            CommandsRules.DamageSummary([Refusal()], path));
    }

    static DamageRecord Refusal() =>
        new(12.5, "refused", "SplittingSteel", 0x0f3f, 0, "", 0, "", 0, 0, "", false,
            "no available projectile recipe/metadata", 0, 0, 0);

    static DamageRecord Hit(int raw, int taken, bool critical = false) =>
        new(1, "outgoing", "Fireball", 0x1234, 0x4d2, "Rhoa", 68, "rare", raw, taken, "fire:12", critical,
            "hit", 900, 900 - (uint)taken, 3.5);
}
