using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// A spawned Shaper or Elder stood next to the player and did nothing at all (live 2026-11-10, area 1_1_2; the same
// spawn in MapWorldsShapersRealm looked like it worked, but its swings still never reached the player). The controller
// is driven in both places: it reaches Act and orders its first swing. One guard between the controller and the client
// then swallows every ordered swing while the player still has entry protection: Game.InGracePeriod in
// Monsters.MonsterSwing (Monsters.cs:1426), which the ordinary monsters' AI passes through as well. This test drives
// Step with a hand-set clock and a real socket and tells the two halves apart: the swing the controller orders reaches
// the wire, and the same swing stays off it while the player is protected. Both phases are decided by the send gate,
// so neither waits on real time.
public sealed class AtlasBossDriveTests
{
    const uint BossId = 0x80000d4;
    const string Shaper = "Metadata/Monsters/AtlasBosses/TheShaperBoss";

    [Fact]
    public async Task A_controller_swing_reaches_the_wire_only_outside_entry_protection()
    {
        // Phase 1: nobody is protected, so the controller's first swing crosses the wire.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var accepting = listener.AcceptTcpClientAsync();
        client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await accepting;

        var serverCiphers = new ConnectionCiphers(new byte[64], isClient: false);
        var clientCiphers = new ConnectionCiphers(new byte[64], isClient: true);
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        try
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());

            var (conn, boss) = MeleeSetup(server.GetStream(), serverCiphers);
            var fight = new ShaperFight(BossId, boss, conn);

            // The hand-set clock: Step's own `now` is what the rotation reads, so one step well past 0 is enough for
            // the melee branch (the variety's AttackRange is 12 and the player stands 5 cells away).
            await fight.Step(conn, 5.0);
            Assert.True(boss.ActionGeneration > 0, "the controller ordered no action at all");

            // The wire carries it: the 0x1ab the client needs, decrypted back to the frame the client would read.
            var header = await ReadFrame(client.GetStream(), clientCiphers, 2);
            Assert.Equal(0x01ab, (ushort)(header[0] << 8 | header[1]));
            // MonsterSwing's own order (Monsters.MonsterSwingOrderLocked) is what stamps these.
            Assert.True(boss.SwingGeneration > 0, "the swing never went through the send gate");
            int swung = boss.SwingGeneration!.Value;

            // Phase 2, on a socket of its own so that nothing of phase 1 can be read here: the same fight with entry
            // protection up, started exactly as PlayerEnteredArea starts it. Monsters.MonsterSwing returns before it
            // orders anything while this is up, so the live windows (every one of them inside a fresh ~60 s window)
            // saw no swing at all.
            using var protectedListener = new TcpListener(IPAddress.Loopback, 0);
            protectedListener.Start();
            using var protectedClient = new TcpClient();
            var protectedAccepting = protectedListener.AcceptTcpClientAsync();
            protectedClient.Connect(IPAddress.Loopback, ((IPEndPoint)protectedListener.LocalEndpoint).Port);
            using var protectedServer = await protectedAccepting;

            var (protectedConn, protectedBoss) = MeleeSetup(protectedServer.GetStream(), serverCiphers);
            protectedConn.EntryProtection.Start(0, (100, 100), GracePeriod.BuffRow, town: false, hideout: false);
            Assert.True(Game.InGracePeriod(protectedConn));
            var protectedFight = new ShaperFight(BossId, protectedBoss, protectedConn);

            // A step well past the first swing's cooldown, so the rotation reaches the melee branch again.
            await protectedFight.Step(protectedConn, 15.0);

            // The controller still drives: the same step that put 0x1ab on the wire above orders its swing here too
            // (the fight's own trace prints "swing 481 returned after 0 ms"). Only the wire is silent, and the send
            // gate is what says so: a swing that passed it would have stamped SwingGeneration.
            Assert.True(protectedBoss.ActionGeneration > 0, "the controller ordered no action at all");
            Assert.Null(protectedBoss.SwingGeneration);
            Assert.False(protectedClient.GetStream().DataAvailable,
                "a swing reached the client despite the player's entry protection");
            Assert.Equal(swung, boss.SwingGeneration);
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
        }
    }

    // A connection whose player stands five cells from a spawned Shaper, in a real socket: the boss monster
    // SpawnMonster builds for /spawn_mob Metadata/Monsters/AtlasBosses/TheShaperBoss@83 unique (the variety's row, its
    // life, its three life thresholds), placed in the connection's own monster table under the id the live run used.
    static (ClientState Conn, SpawnedMonster Boss) MeleeSetup(NetworkStream toClient, ConnectionCiphers serverCiphers)
    {
        var conn = new ClientState(new(1, "boss-drive", 100, 100, 100, 100, null, []), 1, 1)
        {
            PlayerLife = 1000, PlayerSent = true, Character = "BossDrive",
        };
        conn.Wire = ("127.0.0.1:1", toClient, serverCiphers);
        conn.Walk.Teleport(100, 100, 0);
        var variety = new MonsterCatalog().Find(Shaper);
        Assert.NotNull(variety);
        Assert.True(AtlasBosses.IsBoss(variety));
        var boss = new SpawnedMonster("The Shaper@83", new PlayerWalk(100, 95, 20, null, size: 2), 60, 6, maxLife: 3129484)
        {
            Variety = variety, Level = 83, Life = 3129484, MaxLife = 3129484, LifePhases = new([75, 50, 25]),
        };
        conn.Monsters[BossId] = boss;
        return (conn, boss);
    }

    // The frame the client would read, awaited as long as it takes: a phase that expects one never times out.
    static async Task<byte[]> ReadFrame(NetworkStream stream, ConnectionCiphers ciphers, int length)
    {
        var bytes = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = await stream.ReadAsync(bytes.AsMemory(read));
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
        ciphers.Receive.Process(bytes);
        return bytes;
    }
}
