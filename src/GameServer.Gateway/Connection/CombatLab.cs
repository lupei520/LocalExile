using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

// Optional boundary supplied by a game module: the in-game development lab. Agents and scripts use it to exercise the
// ordinary skill pipeline on a character that is really in a world, and to read what each hit actually did, instead of
// driving a mouse or guessing from two life readings. The transport and the framework implement none of it.
// Implemented by the Game module (GameLogic); Agents/docs/dev/combat-lab.md.
interface ICombatLabCommands
{
    /// <summary>The id of one of this connection's own gems, by id or by name (a gem name, or part of one); null when
    /// nothing matches or several do.</summary>
    ushort? SkillIdByName(ClientState player, string name);

    /// <summary>One line per castable gem of this connection: its id, name, level, quality, whether it needs a target,
    /// and whether it can be used right now (cooldown, mana, requirements). This is how a caller discovers what it may
    /// pass to <see cref="UseSkill"/>.</summary>
    string Skills(ClientState player);

    /// <summary>A living monster near this connection, by id or by label; null when nothing matches or several do.</summary>
    uint? MonsterIdByName(ClientState player, string name);

    /// <summary>
    /// Uses one of the character's own gems through the ordinary skill path, exactly as the client's own click would:
    /// cooldowns, mana, reach, accuracy, ailments and the combat commit all still apply. <paramref name="target"/> is a
    /// monster id or label, or null to aim at the ground cell (<paramref name="x"/>, <paramref name="y"/>), or at the
    /// character's own cell when those are 0. Answers a one-line receipt: what was used, at what, and whether the
    /// ordinary pipeline accepted it.
    /// </summary>
    Task<string> UseSkill(ClientState player, string skill, string? target, int x, int y);

    /// <summary>Spawns a monster for a test near the player (a MonsterVarieties row, Id or name; level 1-100; rarity
    /// normal/magic/rare/unique; a custom name). Answers where it stands and its id.</summary>
    Task<string> SpawnTarget(ClientState player, string monster, int level, string rarity, string? name, int dx, int dy, bool friendly);

    /// <summary>Puts one of the character's own inventory gems into a socket of an equipped item through the ordinary
    /// server-side socket operation (the one the client's C→S 0x30 performs), sending the same inventory-change
    /// packets, so the gem is really socketed and its skill appears in /skills. <paramref name="item"/> names the
    /// equipped item by its item id or by a substring of its name; null takes the first equipped item with a free
    /// socket. <paramref name="socket"/> is the socket index, or null for the first free one. Answers one short line
    /// naming the gem, the item and the socket, or a readable reason it could not (no such gem in the inventory, no
    /// free socket, or the gem's requirements as the game reports them).</summary>
    Task<string> SocketGem(ClientState player, string gem, string? item, int? socket);

    /// <summary>The last <paramref name="count"/> damage records of this connection, one compact JSON object per line,
    /// oldest first. <paramref name="filter"/> keeps records whose skill or target contains it (case ignored).</summary>
    string DamageJson(ClientState player, int count, string? filter);

    /// <summary>Forgets this connection's recorded damage.</summary>
    void ClearDamage(ClientState player);
}
