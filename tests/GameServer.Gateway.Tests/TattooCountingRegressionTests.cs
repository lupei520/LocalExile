using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// The counting tattoos' aggregate pass grants each allocated copy its own flat pool. The pool is the sum of the
// copies, so a Warrior's Tale must scale the sum once, not scale the sum and multiply it by the copy count again.
public sealed class TattooCountingRegressionTests
{
    static int Life(IReadOnlyDictionary<uint, int> stats) =>
        stats.GetValueOrDefault(Program.Stats.Key("base_maximum_life")!.Value);

    static int Recovery(IReadOnlyDictionary<uint, int> stats, string stat) =>
        stats.GetValueOrDefault(Program.Stats.Key(stat)!.Value);

    [Fact]
    public void Two_journey_body_tattoos_grant_the_pool_twice_not_four_times()
    {
        var allocated = new HashSet<ushort> { 100, 200 };
        var tattoos = new Dictionary<ushort, string>
        {
            [100] = "Journey Tattoo of the Body",
            [200] = "Journey Tattoo of the Body",
        };
        Assert.Equal(50, Life(Game.TattooCountingStats(tattoos, allocated)));
        // One of the two sits inside a Warrior's Tale: 25 + 50 = 75, not 2 * 75.
        var effect = new Dictionary<ushort, int> { [100] = 100 };
        Assert.Equal(75, Life(Game.TattooCountingStats(tattoos, allocated, effect)));
    }

    [Fact]
    public void Two_ancestral_bloodlines_tattoos_grant_the_recovery_per_distinct_tribe_once()
    {
        var allocated = new HashSet<ushort> { 100, 200, 300 };
        var tattoos = new Dictionary<ushort, string>
        {
            [100] = "Ancestral Tattoo of Bloodlines",
            [200] = "Ancestral Tattoo of Bloodlines",
            [300] = "Tattoo of the Ngamahu Firewalker",
        };
        var stats = Game.TattooCountingStats(tattoos, allocated);
        Assert.Equal(2, Recovery(stats, "life_recovery_rate_+%"));
        Assert.Equal(2, Recovery(stats, "mana_recovery_rate_+%"));
        Assert.Equal(2, Recovery(stats, "energy_shield_recovery_rate_+%"));
        // One Warrior's Tale in the ancestral copies' radii scales the flat pool once.
        var scaled = Game.TattooCountingStats(tattoos, allocated, new Dictionary<ushort, int> { [100] = 100 });
        Assert.Equal(4, Recovery(scaled, "life_recovery_rate_+%"));
    }
}
