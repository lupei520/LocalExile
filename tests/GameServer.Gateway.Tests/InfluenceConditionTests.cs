namespace GameServer.Gateway.Tests;

// The EquippedItem conditions of Stats.dat (the "_if_N_<influence>_items" rows): each one names the effect it gives
// once the character wears enough influenced items, and the effect has to be a stat this build has, or the condition
// holds and the value still lands on nothing.
public sealed class InfluenceConditionTests
{
    [Fact]
    public void Every_equipped_item_condition_maps_onto_a_stat_this_build_has()
    {
        int conditions = 0;
        foreach (string name in Program.Stats.Names)
        {
            if (!InfluencedItemConditions.Read(name, out string target, out _, out int required)) continue;
            conditions++;
            Assert.True(required is 2 or 4 or 6, $"{name} asks for {required} items");
            foreach (string stat in InfluencedItemConditions.Targets(target))
                Assert.True(Program.Stats.Has(stat), $"{name} maps onto the missing stat {stat}");
        }
        // Six influences, three effects at each of the 2, 4 and 6 thresholds.
        Assert.Equal(54, conditions);
    }
}
