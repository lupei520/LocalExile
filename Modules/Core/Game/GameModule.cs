using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using Rules = global::Game;

namespace GameServer.Modules.Game;

/// <summary>The game's rules: gives the framework its <see cref="IGameLogic"/>.</summary>
public sealed class GameModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        Rules.CombatLifetime = module.Lifetime;
        Program.SetGameLogic(new GameLogic(module.Lifetime));
        module.OnTick((player, _) => global::BrineKing.Tick(Program.ConnOf(player)));
        // The Shaper and the Elder: one scripted step per connection per tick, their own fight state per monster.
        module.OnTick((player, _) => global::AtlasBosses.Tick(Program.ConnOf(player)));
        module.Lifetime.Own(GameEvents.PlayerStatSources.Add("BrineKing", global::BrineKing.PlayerStats));
        module.Lifetime.Own(global::BrineKing.CleanupOnUnload());
        module.Lifetime.Own(global::AtlasBosses.CleanupOnUnload());
        module.Lifetime.Own(GameEvents.Kill.Add("Game", Rules.ReservationKill));
        module.OnPlayerLeaving((player, _) => global::BrineKing.Leaving(Program.ConnOf(player)));
    }
}

sealed partial class GameLogic(IModuleLifetime? lifetime = null) : IGameLogic, IControlledPlayerRules, IControlledPlayerCommands
{
    internal IModuleLifetime? Lifetime { get; } = lifetime ?? Rules.CombatLifetime;
    public double MaximumLeaderDistance => Rules.ControlledFollowerLeash;
    public void QueueControlledIntent(ClientState player, ControlledPlayerIntent intent) => Rules.QueueControlledIntent(player, intent);
    public void ClearControlledIntent(ClientState player) => Rules.ClearControlledIntent(player);
    public object ObserveControlledPlayer(ClientState player, ClientState leader) => Rules.ObserveControlledPlayer(player, leader);
    public void ResumePlayerUpdates(ClientState conn)
    {
        if (ReferenceEquals(Program.GameLogicOrNull, this) && ReferenceEquals(Lifetime, Rules.CombatLifetime))
            Rules.EnsurePlayerUpdates(conn);
    }
    public Task OnClientMessages(string remote, NetworkStream stream, ConnectionCiphers ciphers, IReadOnlyList<(ushort Id, byte[] Bytes)> messages, byte[] unknown, ClientState conn, long readAt) =>
        Rules.OnClientMessages(remote, stream, ciphers, messages, unknown, conn, readAt);

    public void Admitting(ClientState conn) => Rules.RegisterVitalsAdmission(conn);

    public int PrepareCharacter(string remote, ClientState conn, int level)
    {
        Rules.RefuseInterruptedHardcoreDeath(conn);
        level = Rules.PrepareCharacterForArea(remote, conn, level);
        // Restore mode vitals before any admitted-area send can fail and enter save cleanup.
        Rules.InitializeCharacterVitals(conn);
        return level;
    }

    public IReadOnlyDictionary<uint, int>? AreaLeagueStats(ClientState conn) => Rules.LeagueStatsFor(conn);

    public async Task Entered(string remote, ClientState conn, int level, int ticketLevel)
    {
        if (level != ticketLevel)
        {
            // The saved experience is the character's level: Auth's (the ticket's) was not updated at the last level-up.
            var fixedLevel = await Rules.SynchronizeAccountLevel(conn);
            Console.WriteLine($"[gateway] {remote} {Program.CharacterName(conn)}: level {level} from the saved experience (the account had {ticketLevel})" +
                (fixedLevel.Ok ? "" : $"; not saved to the account: {fixedLevel.Error}"));
        }
        Console.WriteLine($"[gateway] {remote} {Program.CharacterName(conn)}: {Rules.SkillsFor(conn).ClassFor(conn.ClassRow).ObjectType} (Characters row {conn.ClassRow}), level {conn.Pools.Level} (experience {conn.Experience}, {conn.Passives.Count} passive(s), life {conn.Pools.Life}, mana {conn.Pools.Mana}, attack speed +{conn.Pools.AttackSpeedPct}%), league {conn.League ?? "?"}");
    }

    public void Closing(ClientState conn)
    {
        foreach (var close in GameEvents.TransientClosing.For(conn)) close(conn);
        conn.PaidChannel = null;
        conn.NonChaosDotBypassCarry = conn.NonChaosDotTakenCarry = 0;
        conn.RetirePlayerUpdates();
        Rules.FortificationOf(conn).Clear();
        Rules.LeechOf(conn).Clear(conn.Clock.Elapsed.TotalSeconds);
        conn.Rage.Clear();
        conn.Rampage.Clear();
        conn.RetireOwnedCombat();
        conn.RetireResourceEffects();
        conn.VaalPreventionUntil = 0;
    }

    public void Save(ClientState conn, bool prepared)
    {
        // A refused admission must not load/start inventories or flush a previous connection's cached copy.
        if (prepared)
        {
            CharacterSaves.SaveExperience(Program.CharacterName(conn));
            Rules.ItemsOf(conn).SaveUnsaved();
            Rules.PersistVitals(conn);
            Rules.SaveGolems(conn);
        }
        Rules.CommitHardcoreDeath(conn);
    }

    public void Left(ClientState conn)
    {
        Rules.ForgetVitalsAdmission(conn);
        // The instance keeps its monsters as they are now, for the next connection (#12); the player's minions go with him.
        if (conn.MonstersPlaced) Rules.KeepInstanceMonsters(conn);
    }

    public IDisposable ProjectileMutation(ClientState conn) => Rules.ProjectileMutation(conn, advance: false);
    public IConnectionPlayer PlayerFor(ClientState conn) => new Rules.ModPlayer(conn);
    public IPlayerWorld WorldFor(ClientState conn) => new Rules.ModuleWorld(conn);
    public IEnumerable<(string Value, string Detail)>? CompletionValues(string kind) => Rules.CompletionValues(kind);
    public double PlayerActionFactor(ClientState conn) => Rules.PlayerActionFactor(conn);
    public CharacterSaves.Save BuildControlled(GameServer.Core.Accounts.CharacterClass characterClass, int level) => Rules.BuildControlledPlayer(characterClass, level);
    public Task InitializeControlled(ClientState player, ClientState leader) => Rules.InitializeControlledPlayer(player, leader);
    public Task StepControlled(ClientState player, ClientState leader) => Rules.StepControlledPlayer(player, leader);
    public Task ControlledCompletion(ClientState player) => player.PlayerUpdatesState is PlayerUpdatesState updates ? updates.Completion : Task.CompletedTask;
}

// The in-game combat lab: the chat commands of the Commands module reach the ordinary skill path and the damage
// ledger through this boundary (src/GameServer.Gateway/Connection/CombatLab.cs, Agents/docs/dev/combat-lab.md).
sealed partial class GameLogic : ICombatLabCommands
{
    public ushort? SkillIdByName(ClientState player, string name) => Rules.SkillMatchByName(player, name).Id is var id and not 0 ? id : null;

    public string Skills(ClientState player) => Rules.SkillsListing(player);

    public uint? MonsterIdByName(ClientState player, string name) => Rules.MonsterMatchByName(player, name).Id is var id and not 0 ? id : null;

    public async Task<string> UseSkill(ClientState player, string skill, string? target, int x, int y)
    {
        if (Rules.SkillMatchByName(player, skill) is not { Id: not 0 } gem)
            return $"no gem of yours matches \"{skill}\"";
        uint targetId = 0;
        string aimed;
        if (target is not null)
        {
            var monster = Rules.MonsterMatchByName(player, target);
            if (monster.Id == 0)
                return monster.Matches.Count == 0
                    ? $"no living monster near you matches \"{target}\""
                    : $"\"{target}\" matches {monster.Matches.Count} monsters: {string.Join(", ", monster.Matches)}";
            targetId = monster.Id;
            aimed = $"monster 0x{monster.Id:x} ({monster.Label})";
        }
        else if (x != 0 || y != 0) aimed = $"cell ({x}, {y})";
        else
        {
            lock (player.Monsters) (x, y) = player.Walk.Position(player.Clock.Elapsed.TotalSeconds);
            targetId = PlayerId;   // no target: the character's own cell, the ObjectId form the client uses on itself
            aimed = "yourself";
        }
        await Rules.UseSkillThroughPipeline(player, gem.Id, targetId, x, y);
        return $"cast {gem.Name} (0x{gem.Id:x4}) at {aimed} - the ordinary pipeline may still refuse it, read /damage or /state for the outcome";
    }

    public async Task<string> SpawnTarget(ClientState player, string monster, int level, string rarity, string? name, int dx, int dy, bool friendly)
    {
        if (Rules.Monsters.Error is { } error) return error;
        if (Rules.Monsters.Find(monster) is not { } variety) return $"no monster matches \"{monster}\"";
        if (!variety.Supported) return $"{variety} is not supported yet";
        if (!Enum.TryParse(rarity, ignoreCase: true, out GameServer.Core.Protocol.Instance.MonsterRarity parsed))
            parsed = GameServer.Core.Protocol.Instance.MonsterRarity.Normal;
        return await Rules.SpawnLabMonster(player, variety, level, parsed, name, dx, dy, friendly);
    }

    public string DamageJson(ClientState player, int count, string? filter) => Rules.DamageLog(player, count, filter);

    public void ClearDamage(ClientState player) => DamageLedger.Clear(player);

    // The lab's own socket operation, for a module (PlayerBots) that wants to choose the skill under test; the chat
    // command /socket_gem reaches the same Rules.SocketGemForLab through the Commands module's static call.
    public Task<string> SocketGem(ClientState player, string gem, string? item, int? socket) =>
        Rules.SocketGemForLab(player.Wire.Remote, player.Wire.Stream, player.Wire.Ciphers, player, gem, item, socket);
}
