namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Where clients reach GameServer.Gateway: the address Auth sends in login 0x14 (the first area) and the Gateway in
/// instance 0x27 (every area change), and the port the Gateway listens on: localexile.conf Gateway.PublicAddress (default
/// 127.0.0.1, an IP address or a tunnel name) and Gateway.Port (default 6112), or Game_GATEWAY_HOST / Game_GATEWAY_PORT
/// (ServerConfig); scripts/dev/live.ps1 gives both servers the same values, so a stack on its own ports (-PortBase) sends
/// its clients to its own Gateway.
/// A tunnel that maps a different public port (frp and similar) sets Game_GATEWAY_PUBLIC_PORT to the public one: Auth
/// tells clients that port while the Gateway keeps listening on Game_GATEWAY_PORT.
/// </summary>
public static class GatewayEndpoint
{
    public const string DefaultHost = "127.0.0.1";
    public const ushort DefaultPort = 6112;

    public static string Host => ServerConfig.GatewayPublicAddress;

    public static ushort Port => (ushort)ServerConfig.GatewayPort;

    /// <summary>The port written into 0x14/0x27: <see cref="Port"/> unless a tunnel maps another public one.</summary>
    public static ushort PublicPort => ushort.TryParse(Environment.GetEnvironmentVariable("Game_GATEWAY_PUBLIC_PORT"), out var port) && port > 0 ? port : Port;
}
