using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

/// <summary>
/// /smite and /mob_life (Modules/Core/Game/Combat/CombatLabRules.cs): the deliberate damage source that lets an
/// endgame boss be driven through a phase without a character that can out-damage 75 million life. The hit must go
/// through the ordinary ApplyMonsterHitLocked — so the life line moves, the ledger records it, and the monster's own
/// invulnerability/phase/stage rules still apply — while /mob_life's value rules refuse loudly instead of clamping.
/// </summary>
public sealed class CombatLabSmiteTests
{
    static ClientState Conn() => new(new(1, "smite-lab", 100, 100, 10, 10, null, []), 1, 1);

    [Fact]
    public void Smite_types_and_mob_life_values_are_resolved_with_a_refusal()
    {
        Assert.True(Game.TryDamageType(null, out var physical));
        Assert.Equal(DamageType.Physical, physical);
        Assert.True(Game.TryDamageType("Fire", out var fire));
        Assert.Equal(DamageType.Fire, fire);
        Assert.False(Game.TryDamageType("shadow", out _));      // not a damage type at all
        Assert.False(Game.TryDamageType("5", out _));            // a number is not a damage type's name

        // /mob_life: an absolute value and a percentage, then the refusals the command promises.
        Assert.True(Game.TryMobLife("500", 1000, out uint absolute, out _));
        Assert.Equal(500u, absolute);
        Assert.True(Game.TryMobLife("25%", 1000, out uint quarter, out _));
        Assert.Equal(250u, quarter);
        Assert.True(Game.TryMobLife("0%", 1000, out uint zero, out _));
        Assert.Equal(0u, zero);
        Assert.False(Game.TryMobLife("101%", 1000, out _, out string? over));
        Assert.Contains("0 to 100", over);
        Assert.False(Game.TryMobLife("1001", 1000, out _, out string? above));
        Assert.Contains("above its maximum", above);
        Assert.False(Game.TryMobLife("-1", 1000, out _, out _));
        Assert.False(Game.TryMobLife("half", 1000, out _, out _));
    }

    [Fact]
    public void A_smite_moves_the_life_and_leaves_an_outgoing_ledger_row()
    {
        var conn = Conn();
        var m = new SpawnedMonster("Rhoa Mare@20", new PlayerWalk(10, 10, 1), 60, 2, maxLife: 1000) { Life = 1000 };
        conn.Monsters[7] = m;

        var smite = Game.SmiteLocked(conn, 7, m, 400, DamageType.Fire, 12.5);
        Assert.True(smite is not null);
        Assert.Equal(1000u, smite!.Value.Before);
        Assert.Equal(600u, smite.Value.After);
        Assert.Equal(600u, m.Life);   // the life really dropped, through the ordinary hit path

        var record = Assert.Single(DamageLedger.Recent(conn, 50));
        Assert.Equal("outgoing", record.Kind);
        Assert.Equal("dev-smite", record.Skill);
        Assert.Equal(7u, record.TargetId);
        Assert.Equal("Rhoa Mare@20", record.Target);
        Assert.Equal(400, record.Raw);
        Assert.Equal(400, record.Taken);          // no overkill: raw == taken, lifeAfter == before - taken
        Assert.Equal(1000u, record.LifeBefore);
        Assert.Equal(600u, record.LifeAfter);
        Assert.Equal("fire:400", record.Types);   // what the injected hit was made of, as /damage prints it
        Assert.False(record.Critical);
        Assert.Equal("hit", record.Outcome);
    }
}
