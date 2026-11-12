using System.Text.Json;

namespace GameServer.Gateway.Tests;

/// <summary>
/// A use the ordinary skill path accepted but never ran (Modules/Core/Game/Combat/CombatLab.cs NoteRefusal, and the
/// gates that call it). Live 2026-11-09: "/cast Splitting Steel 0x8000001" printed a receipt, then left no record and
/// no log line anywhere while two monsters were hitting the character — a harness cannot tell that from a hit that
/// landed for zero. A dropped use now leaves a ledger record and the receipt names it.
/// </summary>
public sealed class CombatLabDropTests
{
    static ClientState Conn() => new(new(1, "drop-lab", 100, 100, 10, 10, null, []), 1, 1);

    const string Reason = "the character was in hit recovery, so the use was dropped";

    [Fact]
    public void A_dropped_use_is_recorded_and_the_receipt_says_why()
    {
        var conn = Conn();
        // Before the use, the ledger holds nothing: the receipt says so and claims no hit.
        Assert.Equal("; nothing recorded yet - read /damage (a projectile may still be in flight)",
            CombatLab.LabNote(conn, CombatLab.NewestLabRecord(conn)));

        var before = CombatLab.NewestLabRecord(conn);
        Game.NoteRefusal(conn, 0x0105, -1, null, Reason, label: "SplittingSteel");

        var record = Assert.Single(DamageLedger.Recent(conn, 1));
        Assert.Equal("refused", record.Kind);
        Assert.Equal("SplittingSteel", record.Skill);
        Assert.Equal(0x0105, record.SkillId);
        Assert.Equal(Reason, record.Outcome);
        Assert.Equal(0, record.Raw);
        Assert.Equal(0, record.Taken);

        // The same record as the JSON line a script reads: no damage, the reason in "outcome".
        using var json = JsonDocument.Parse(DamageLedger.Json(record));
        Assert.Equal("refused", json.RootElement.GetProperty("kind").GetString());
        Assert.Equal(Reason, json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("raw").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("taken").GetInt32());

        // And the receipt now names the refusal instead of only promising that a refusal is possible.
        Assert.Equal($"; the ledger holds a refusal: {Reason}", CombatLab.LabNote(conn, before));
    }

    [Fact]
    public void A_monsters_hit_is_not_read_as_the_use_s_outcome()
    {
        var conn = Conn();
        var before = CombatLab.NewestLabRecord(conn);
        // The two monsters the live case was fighting hit the character while the use was handed over.
        DamageLedger.Note(conn, new DamageRecord(3.5, "incoming", "Rhoa Mare@20", 0x1234, 0x8000001u,
            "Exile", 20, "", 40, 40, "physical:40", false, "hit", 900, 860, 0));

        // An incoming record is not evidence about the use: the receipt must not report it as the use's outcome.
        Assert.Equal("; nothing recorded yet - read /damage (a projectile may still be in flight)",
            CombatLab.LabNote(conn, before));
    }
}
