using GameServer.Modules.Delve;

namespace GameServer.Gateway.Tests;

// The Delve's mine level is the client's own depth curve (data/tables/DelveLevelScaling.json "MonsterLevel"): it rises
// quickly in the first depths and stays at its cap. A made-up ramp made the mine's monsters far weaker than the game
// has them in the middle of the chart.
public sealed class DelveDepthTests
{
    [Fact]
    public void The_mine_level_follows_the_client_depth_curve()
    {
        Assert.Equal(34, DelveMine.MonsterLevel(1));
        Assert.Equal(71, DelveMine.MonsterLevel(50));
        Assert.Equal(83, DelveMine.MonsterLevel(149));
        // The curve's cap holds for the whole rest of the chart.
        Assert.Equal(83, DelveMine.MonsterLevel(6000));
    }
}
