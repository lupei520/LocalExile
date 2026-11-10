using System.Text.Json;
using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

/// <summary>
/// The combat lab's ledger (Modules/Core/Game/Combat/DamageLedger.cs, Agents/docs/dev/combat-lab.md). A ClientState is
/// cheap to build but a real SpawnedMonster, skill and connection are not: these pin the two things a script depends
/// on — the cap keeps the newest records, and one record's JSON line carries every field a formula comparison reads.
/// </summary>
public sealed class CombatLabTests
{
    static ClientState Conn() => new(new(1, "damage-lab", 100, 100, 10, 10, null, []), 1, 1);

    [Fact]
    public void Ledger_keeps_the_newest_records_and_caps_at_capacity()
    {
        var conn = Conn();
        int over = DamageLedger.Capacity + 7;
        for (int i = 0; i < over; i++) DamageLedger.Note(conn, Record(at: i));

        var recent = DamageLedger.Recent(conn, DamageLedger.Capacity);
        Assert.Equal(DamageLedger.Capacity, recent.Count);
        Assert.Equal(7, recent[0].At);                       // the oldest seven fell off
        Assert.Equal(over - 1, recent[^1].At);
        // Asking for more than the cap still returns only what the ledger holds, newest last.
        Assert.Equal(DamageLedger.Capacity, DamageLedger.Recent(conn, int.MaxValue).Count);
        Assert.Equal(3, DamageLedger.Recent(conn, 3).Count);
        Assert.Equal(over - 1, DamageLedger.Recent(conn, 3)[^1].At);

        DamageLedger.Clear(conn);
        Assert.Empty(DamageLedger.Recent(conn, 50));
    }

    [Fact]
    public void Json_line_carries_every_field_and_the_overkill_invariant()
    {
        // raw is the hit's resolved damage; taken is the life it actually took. A non-overkill hit has them equal, an
        // overkill one has raw > taken == lifeBefore.
        var hit = Record(at: 12.5, raw: 431, taken: 431, lifeBefore: 900, lifeAfter: 469);
        Assert.Equal(hit.Raw, hit.Taken);

        var overkill = Record(at: 13, raw: 431, taken: 200, lifeBefore: 200, lifeAfter: 0);
        Assert.True(overkill.Raw > overkill.Taken);

        var line = DamageLedger.Json(hit);
        Assert.DoesNotContain('\n', line);
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        Assert.Equal(12.5, root.GetProperty("at").GetDouble());
        Assert.Equal("outgoing", root.GetProperty("kind").GetString());
        Assert.Equal("Fireball", root.GetProperty("skill").GetString());
        Assert.Equal(0x1234, root.GetProperty("skillId").GetUInt16());
        Assert.Equal(0x4d2u, root.GetProperty("targetId").GetUInt32());
        Assert.Equal("Rhoa", root.GetProperty("target").GetString());
        Assert.Equal(68, root.GetProperty("level").GetInt32());
        Assert.Equal("rare", root.GetProperty("rarity").GetString());
        Assert.Equal(431, root.GetProperty("raw").GetInt32());
        Assert.Equal(431, root.GetProperty("taken").GetInt32());
        Assert.Equal("physical:12,cold:30", root.GetProperty("types").GetString());
        Assert.True(root.GetProperty("crit").GetBoolean());
        Assert.Equal("hit", root.GetProperty("outcome").GetString());
        Assert.Equal(900u, root.GetProperty("lifeBefore").GetUInt32());
        Assert.Equal(469u, root.GetProperty("lifeAfter").GetUInt32());
        Assert.Equal(3.5, root.GetProperty("distance").GetDouble());
    }

    static DamageRecord Record(double at, int raw = 431, int? taken = null, uint lifeBefore = 900,
        uint lifeAfter = 469) =>
        new(at, "outgoing", "Fireball", 0x1234, 0x4d2, "Rhoa", 68, "rare", raw, taken ?? raw,
            "physical:12,cold:30", true, "hit", lifeBefore, lifeAfter, 3.5);
}
