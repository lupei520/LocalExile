using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

// The channelled sweep's centre (11-skills.md §10): the client draws Cyclone's spin around the caster, so the skill's
// own hit lands on the player's cell even when the use was aimed within the weapon's reach, while an ordinary melee
// skill's hit keeps its aimed point there (live 2026-11-09). And a channelled use is not paced by the shared attack
// timer: a use that arrives while another attack swings starts the channel (the client has already started its spin),
// and one that arrives while the channel runs keeps it instead of paying for a second one.
public sealed class ChannelledSweepTests : IDisposable
{
    readonly string database = Path.Combine(Path.GetTempPath(), $"localexile-cyclone-{Guid.NewGuid():N}.db");
    readonly IGameLogic previousLogic = Program.GameLogicOrNull!;
    readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
    readonly ModuleLifetime lifetime = new("Game");
    readonly ClientState conn;
    readonly TcpListener listener;
    readonly TcpClient writer;
    readonly NetworkStream stream;
    readonly ConnectionCiphers ciphers;
    bool openedSaves;
    Task? drain;
    TextWriter previousOut = Console.Out;
    StringWriter console = new();

    public ChannelledSweepTests()
    {
        conn = new ClientState(new(1, "cyclone", 300, 300, 100, 100, null, []), 1, 1)
        {
            Instance = new(), Character = $"cyclone-{Guid.NewGuid():N}", PlayerLife = 1000, PlayerMana = 1000,
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
            "virtual_intelligence_for_requirements" }) conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        writer = new TcpClient();
        var accepting = listener.AcceptTcpClientAsync();
        writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port).Wait();
        stream = writer.GetStream();
        accepting.Wait(1000);
        // Drain everything the server sends: the socket would otherwise refuse writes.
        drain = Task.Run(async () =>
        {
            var reader = accepting.Result.GetStream();
            var buffer = new byte[65536];
            try { while (await reader.ReadAsync(buffer) > 0) { } } catch { /* disposed */ }
        });
        // The Gateway narrates its decisions on the console; capture it instead of the packet histories (which hold
        // only hex heads, not the log lines' "attack hit N within 16 cells of (x, y)").
        previousOut = Console.Out;
        console = new StringWriter();
        Console.SetOut(TextWriter.Synchronized(console));
        ciphers = new ConnectionCiphers(new byte[64], isClient: false);
        Saves.Open(database);
        openedSaves = true;
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
    }

    public void Dispose()
    {
        conn.Closed = true;
        Program.InGame.TryRemove(conn, out _);
        PlayerInventories.Forget(conn.Character!);
        lifetime.Stop(ex => throw ex);
        Console.SetOut(previousOut);
        console.Dispose();
        Game.CombatLifetime = previousLifetime;
        Program.SetGameLogic(previousLogic);
        writer.Dispose(); listener.Stop();
        try { drain?.Wait(500); } catch { }
        if (openedSaves) TestEnvironment.ReleaseSaves(database);
    }

    // A cyclone gem socketed in a club (BehemothRuntimeTests' admission shape).
    void EquipCyclone()
    {
        var club = Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/OneHandMaces/OneHandMace1") ?? throw new Exception("no Driftwood Club in the data");
        var gemItem = Program.Items.Find("Metadata/Items/Gems/SkillGemCyclone") ?? throw new Exception("no SkillGemCyclone in the data");
        var items = Game.ItemsOf(conn);
        items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(club) with
        {
            Sockets = [new ItemSocket(SocketColour.White, ItemCatalog.Create(gemItem))],
            Links = [1],
        });
    }

    int CycloneId => Game.GemSkillEffects(conn).First(g => g.Name == "Cyclone").Id;

    async Task Cast(ushort skill, int aimX, int aimY)
    {
        var packet = new LoginWriter().U16(InstanceProtocol.UseSkillAtLocationId)
            .U32((uint)aimX).U32((uint)aimY).U16(skill).U16(1).U16(1033).ToArray();
        await Game.OnClientMessages("cyclone-test", stream, ciphers, [(InstanceProtocol.UseSkillAtLocationId, packet)], [], conn)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    string Sent(string contains) => string.Join("\n", console.ToString().Split('\n')
        .Where(l => l.Contains(contains, StringComparison.Ordinal)));

    [Fact]
    public async Task A_channelled_sweep_lands_on_the_player_wherever_the_use_aims()
    {
        EquipCyclone();
        // The player stands at (100, 100); aim 10 cells east: within the skill's reach, where the old code
        // centred the circle on the aim.
        await Cast((ushort)CycloneId, 110, 100);
        var seen = Sent("attack hit");
        if (!seen.Contains("of (100, 100)")) throw new Exception("CAPTURED<<\n" + console.ToString() + "\n>>");
        Assert.Contains("of (100, 100)", seen);
    }

    [Fact]
    public async Task A_channelled_use_runs_whatever_the_attack_timer_says_and_keeps_its_channel()
    {
        EquipCyclone();
        // A swing still running: an ordinary attack gem would be refused here.
        conn.NextAttackAt = conn.Clock.Elapsed.TotalSeconds + 5;
        await Cast((ushort)CycloneId, 105, 100);
        var seen = Sent("attack hit");
        if (!seen.Contains("of (100, 100)")) throw new Exception("CAPTURED<<\n" + console.ToString() + "\n>>");
        Assert.Contains("of (100, 100)", seen);
        Assert.Equal((ushort)CycloneId, conn.ChannelSkill);
        // A further use while the channel runs keeps it: no refusal, no second cast.
        await Cast((ushort)CycloneId, 105, 100);
        Assert.Equal((ushort)CycloneId, conn.ChannelSkill);
        // The channel's keep-alive log names the skill; no refusal may.
        if (console.ToString().Contains("ignored, the previous attack"))
            throw new Exception("REFUSED<<" + console.ToString() + ">>");
    }
}
