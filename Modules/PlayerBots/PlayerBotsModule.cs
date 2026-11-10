using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.PlayerBots;

/// <summary>Bots the server plays, so a new server feels lived in (BotDirector). On while a league lists the module (no league
/// does by default). Settings in settings.json, the route in story-main.md and story-side.md, the log in logs/ (all beside
/// this file). The economy snapshot of tools/collect_economy.py is in economy/&lt;league&gt;/ for the trading to come.</summary>
public sealed class PlayerBotsModule : IModule
{
    // One director for the process: a hot reload loads this assembly again, and the old one's bots must go first.
    const string Running = "LocalExile.PlayerBots.Stop";

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        (AppContext.GetData(Running) as Action)?.Invoke();
        var settings = module.Settings<BotSettings>();
        var route = BotQuest.Route(module.Folder);
        if (route.Count == 0) { module.Log("bots: no route (story-main.md, story-side.md)"); return; }
        var director = new BotDirector(settings, route, new BotLog(Path.Combine(module.Folder, "logs")), module.Log,
            Path.Combine(module.Folder, "state", "bots.json"));
        AppContext.SetData(Running, (Action)director.Stop);
        module.Lifetime.Own(new StopOnDispose(director.Stop));
        director.Start();
        // Parties with bots (BotParty): the Social module passes on the invites and parties that are not its own.
        module.OnPacket(InstanceSocial.PartyInviteId, async p =>
            await director.Invite(Program.ConnOf(p.Player), InstanceSocial.ReadPartyInvite(p.Bytes.ToArray())) != BotInvite.NotFound);
        module.OnPacket(InstanceSocial.PartyLeaveId, p => director.Leave(Program.ConnOf(p.Player), InstanceSocial.ReadPartyAccept(p.Bytes.ToArray())));
        module.OnPacket(InstanceSocial.PartyKickId, p => director.Kick(Program.ConnOf(p.Player), InstanceSocial.ReadPartyKick(p.Bytes.Span)));
        // '/botinvite <name>': the same invite the party panel sends (C→S 0x93), for a player who names a bot in chat
        // instead of the panel. The Social module's '/invite' only knows real players. '/botleave' takes the party away
        // again, so a bot can be tried out without opening the social panel.
        module.OnChatCommand("botinvite", "/botinvite <bot name> (invite a bot you can see into your party)", async cmd =>
        {
            string name = string.Join(' ', cmd.Args).Trim();
            if (name.Length == 0) { await director.Tell(cmd.Player, "用法：/botinvite <机器人名字>"); return; }
            var result = await director.Invite(Program.ConnOf(cmd.Player), name);
            await director.Tell(cmd.Player, result switch
            {
                BotInvite.Joined => $"{name} 已加入队伍。",
                BotInvite.Refused => $"已经有五个机器人跟着你了，{name} 留在了原地。",
                _ => $"没找到你能看见的机器人 \"{name}\"（名字见 Modules/PlayerBots/state/bots.json）。",
            });
        });
        module.OnChatCommand("botleave", "/botleave (the bots following you leave and go back to their quests)", async cmd =>
        {
            bool left = await director.LeaveAny(Program.ConnOf(cmd.Player));
            await director.Tell(cmd.Player, left ? "机器人已离队，回去做任务了。" : "你队伍里没有机器人。");
        });
    }

    sealed class StopOnDispose(Action stop) : IDisposable
    {
        Action? remaining = stop;
        public void Dispose() => Interlocked.Exchange(ref remaining, null)?.Invoke();
    }
}
