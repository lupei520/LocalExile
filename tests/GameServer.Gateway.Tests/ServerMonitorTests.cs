using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ServerMonitorTests
{
    // The page reads these names, so a rename has to show up here rather than as an empty cell in a browser.
    [Fact]
    public void SnapshotHasTheShapeThePageReads()
    {
        var state = JsonNode.Parse(ServerMonitor.Snapshot())!.AsObject();
        var server = state["server"]!.AsObject();

        Assert.Equal(GatewayEndpoint.Port, (int)server["port"]!);
        Assert.True((long)server["uptimeSeconds"]! >= 0);
        Assert.Equal(0, (int)server["players"]!);
        Assert.True((long)server["memoryMb"]! > 0);
        Assert.Empty(state["players"]!.AsArray());
        Assert.NotNull(state["log"]!.AsArray());
    }

    // The page follows the Gateway's port unless Game_MONITOR_PORT says otherwise, and 0 turns it off.
    [Fact]
    public void PortFollowsTheGatewayUnlessItIsSet()
    {
        string? before = Environment.GetEnvironmentVariable("Game_MONITOR_PORT");
        try
        {
            Environment.SetEnvironmentVariable("Game_MONITOR_PORT", null);
            Assert.Equal(GatewayEndpoint.Port + 1, ServerMonitor.Port);
            Environment.SetEnvironmentVariable("Game_MONITOR_PORT", "23013");
            Assert.Equal(23013, ServerMonitor.Port);
            Environment.SetEnvironmentVariable("Game_MONITOR_PORT", "0");
            Assert.Equal(0, ServerMonitor.Port);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Game_MONITOR_PORT", before);
        }
    }
}
