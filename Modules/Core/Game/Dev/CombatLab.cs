using System.Globalization;
using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

/// <summary>
/// The combat lab's dev commands (Agents/docs/dev/combat-lab.md). They own no damage math: /cast hands a normal
/// C→S skill request to the Game module, which runs the ordinary pipeline, and /damage reads back what the ledger
/// recorded. The receipt says the request was handed over; the ledger or /state is the evidence of what landed.
/// <para>They live in the Game module, not in the Commands module: the Commands module holds the commands GGG's
/// servers have, and the lab is a development tool that exercises this module's own boundary.</para>
/// </summary>
static class CombatLab
{

    static ICombatLabCommands? Lab => Program.GameLogicOrNull as ICombatLabCommands;

    /// <summary>/cast's usage, shared with its registration in CommandsFeature. The spaces note stays inside the
    /// "(dev: …)" tail so ChatCommands.ShortUsage's "/commands" page still shows the whole argument list.</summary>
    internal const string CastUsage = "/cast <skill id|name> [<monster id|name> | <x> <y>] "
        + "(dev: use one of your own gems through the ordinary skill path, at a monster, at a cell, or at yourself; "
        + "a gem name may contain spaces, as /skills prints it)";

    /// <summary>/smite's usage: one deliberate hit, for exercising a boss whose life no test character can out-damage.</summary>
    internal const string SmiteUsage = "/smite <monster id|name> <damage> [<type>] "
        + "(dev: one deliberate hit through the ordinary hit path, so the life line, the encounter's phases, the "
        + "death path and the ledger all see it; type is physical, lightning, cold, fire or chaos, default physical)";

    /// <summary>/mob_life's usage: jump straight to a threshold value or percentage.</summary>
    internal const string MobLifeUsage = "/mob_life <monster id|name> <value|n%> "
        + "(dev: set a monster's current life to a value or to n% of its maximum, for jumping to a phase threshold; "
        + "0 runs a lethal dev-smite so the ordinary death handling happens)";

    /// <summary>
    /// Splits /cast's arguments the way a person types them: the LONGEST leading run of arguments that names exactly
    /// one of the character's gems is the skill, and everything after it is the aim. A run that names several gems or
    /// none does not resolve, so a shorter match is never picked silently; a first token in quotes is one token.
    /// <paramref name="match"/> is the caller's SkillMatchByName. When nothing resolves, the returned Skill is the
    /// longest run that named several gems (so the refusal can list them), else the first argument.
    /// "/cast Splitting Steel Rhoa" -> "Splitting Steel" + ["Rhoa"], exactly as "/cast \"Splitting Steel\" Rhoa" does.
    /// </summary>
    internal static (string Skill, string[] Aim) SplitCast(string[] args, Func<string, Game.SkillMatch> match)
    {
        if (args.Length == 0) return ("", []);
        bool quoted = args[0].Length >= 2 && args[0][0] == '"' && args[0][^1] == '"';
        string subject = quoted ? args[0][1..^1] : args[0];
        if (quoted && match(subject) is { Id: not 0 }) return (subject, args[1..]);
        bool named = false;
        for (int take = args.Length; take >= 1; take--)
        {
            string name = string.Join(' ', args[..take]);
            var found = match(name);
            if (found.Id != 0) return (name, args[take..]);
            if (!named && found.Candidates.Count > 0) { subject = name; named = true; }
        }
        return (subject, []);
    }

    internal static async Task CastSkill(ClientState conn, string[] args)
    {
        if (Lab is not { } lab)
        {
            await conn.Commands!.Reply("The game module is not loaded; /cast is unavailable.");
            return;
        }
        if (args.Length == 0) throw new ArgumentException(CastUsage);
        var (skill, rest) = SplitCast(args, name => Game.SkillMatchByName(conn, name));
        var match = Game.SkillMatchByName(conn, skill);
        if (match.Id == 0)
        {
            await conn.Commands!.Reply(match.Candidates.Count == 0
                ? $"No gem of yours matches \"{skill}\". Use /skills for the list."
                : $"\"{skill}\" matches {match.Candidates.Count} gems, say which one: {string.Join(", ", match.Candidates)}");
            return;
        }
        // The aim is a monster id or name, two numbers (x y), or nothing (the character's own cell), as before; a name
        // with spaces is the whole remainder.
        string? target = null;
        int x = 0, y = 0;
        if (rest.Length == 2 && int.TryParse(rest[0], out int ax))
        {
            if (!int.TryParse(rest[1], out int ay)) throw new ArgumentException(CastUsage);
            (x, y) = (ax, ay);
        }
        else if (rest.Length >= 2 && int.TryParse(rest[0], out _)) throw new ArgumentException(CastUsage);
        else if (rest.Length > 0) target = string.Join(' ', rest);
        // The receipt says what the ledger gained from this use, at hand-over time: a refusal names the gate that
        // dropped it, so "accepted" and "0 records" can no longer be the same answer (live 2026-11-09).
        var before = NewestLabRecord(conn);
        string receipt = await lab.UseSkill(conn, match.Id.ToString(CultureInfo.InvariantCulture), target, x, y);
        await conn.Commands!.Reply(receipt + LabNote(conn, before));
    }

    /// <summary>The lab's newest record a use can be judged by — a hit or a refusal, never the monsters' hits on the
    /// player — or null when the ledger holds none yet.</summary>
    internal static DamageRecord? NewestLabRecord(ClientState conn) =>
        DamageLedger.Recent(conn, DamageLedger.Capacity).LastOrDefault(r => r.Kind != "incoming");

    /// <summary>What the ledger gained while the use was handed over. A refusal means the use produced nothing and
    /// says why; a hit means it landed; "nothing yet" is the truth for a projectile still in flight and never a claim
    /// that a hit happened.</summary>
    internal static string LabNote(ClientState conn, DamageRecord? before)
    {
        var after = NewestLabRecord(conn);
        if (ReferenceEquals(before, after))
            return "; nothing recorded yet - read /damage (a projectile may still be in flight)";
        return after!.Kind == "refused"
            ? $"; the ledger holds a refusal: {after.Outcome}"
            : $"; the ledger holds a {after.Kind} record";
    }

    internal static async Task DamageCommand(ClientState conn, string[] args)
    {
        if (Lab is not { } lab)
        {
            await conn.Commands!.Reply("The game module is not loaded; /damage is unavailable.");
            return;
        }
        if (args.Length > 0 && args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            int cleared = CountRecords(conn, DamageLedger.Capacity, null);
            lab.ClearDamage(conn);
            await conn.Commands!.Reply($"damage cleared {cleared} record(s)");
            return;
        }
        // /damage [count] [filter]: a first argument that is not a number is the filter, not an error, so a caller
        // that only has a skill or target name can write /damage Cyclone and keep the default count.
        int count = 50;
        int at = 0;
        if (args.Length > 0 && int.TryParse(args[0], out int parsed))
        {
            count = parsed;
            at = 1;
        }
        count = Math.Clamp(count, 1, DamageLedger.Capacity);
        string? filter = at < args.Length ? string.Join(' ', args[at..]) : null;
        string path = Game.WriteDamageLog(conn, count, filter);
        await conn.Commands!.Reply(DamageSummary(DamagedRecords(conn, count, filter).ToList(), path));
    }

    /// <summary>This connection's last <paramref name="count"/> records whose skill or target contains
    /// <paramref name="filter"/> (case ignored), oldest first: exactly the set <c>/damage</c> writes to the file.</summary>
    internal static IReadOnlyList<DamageRecord> DamagedRecords(ClientState conn, int count, string? filter) =>
        [.. DamageLedger.Recent(conn, count).Where(r => filter is null
            || r.Skill.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || r.Target.Contains(filter, StringComparison.OrdinalIgnoreCase))];

    /// <summary>The lab's one-line summary of a record set. A hit is a recorded hit (Kind "outgoing" or "incoming");
    /// a refusal is a use the ordinary path turned down before any hit (Kind "refused"), named separately so "0
    /// damage" and "never fired" are not the same answer (Agents/docs/dev/combat-lab.md).</summary>
    internal static string DamageSummary(IReadOnlyList<DamageRecord> records, string path)
    {
        int hits = records.Count(r => r.Kind != "refused" && !r.Critical);
        int criticals = records.Count(r => r.Kind != "refused" && r.Critical);
        int refused = records.Count(r => r.Kind == "refused");
        int taken = records.Sum(r => r.Taken);
        return $"damage {records.Count} record(s) -> {path.Replace('\\', '/')} " +
            $"({hits} hit, {refused} refused, {criticals} critical, {taken} taken)";
    }

    /// <summary>/smite &lt;monster id|name&gt; &lt;damage&gt; [&lt;type&gt;]: one deliberate hit, for exercising a boss whose
    /// life no test character can out-damage. The hit goes through the ordinary path (CombatLabRules.SmiteForLab).</summary>
    internal static async Task SmiteCommand(ClientState conn, string[] args,
        string remote, NetworkStream stream, ConnectionCiphers ciphers)
    {
        if (args.Length is < 2 or > 3) throw new ArgumentException(SmiteUsage);
        if (!int.TryParse(args[1], out int damage)) throw new ArgumentException(SmiteUsage);
        await conn.Commands!.Reply(await Game.SmiteForLab(remote, stream, ciphers, conn, args[0], damage,
            args.Length == 3 ? args[2] : null));
    }

    /// <summary>/mob_life &lt;monster id|name&gt; &lt;value|n%&gt;: jump a monster's current life to a threshold value or
    /// percentage (CombatLabRules.MobLifeForLab); 0 is a lethal dev-smite, never a direct write of 0.</summary>
    internal static async Task MobLifeCommand(ClientState conn, string[] args,
        string remote, NetworkStream stream, ConnectionCiphers ciphers)
    {
        if (args.Length != 2) throw new ArgumentException(MobLifeUsage);
        await conn.Commands!.Reply(await Game.MobLifeForLab(remote, stream, ciphers, conn, args[0], args[1]));
    }

    /// <summary>/socket_gem &lt;gem id|name&gt; [&lt;equipped item id&gt;] [&lt;socket index&gt;]: puts one of the character's own
    /// inventory gems into an equipped item's socket through the ordinary socket operation, so a lab test can choose
    /// which skill is under test. The item may also be named by part of its base name.</summary>
    internal static async Task SocketGemCommand(ClientState conn, string[] args,
        string remote, NetworkStream stream, ConnectionCiphers ciphers)
    {
        if (args.Length is 0 or > 3)
            throw new ArgumentException("/socket_gem <gem id|name> [<equipped item id|name>] [<socket index>]");
        string? item = args.Length >= 2 && args[1].Length > 0 ? args[1] : null;
        int? socket = null;
        if (args.Length == 3)
        {
            if (!int.TryParse(args[2], out int index) || index < 0)
                throw new ArgumentException("/socket_gem <gem> <item> <socket index>");
            socket = index;
        }
        await conn.Commands!.Reply(await Game.SocketGemForLab(remote, stream, ciphers, conn, args[0], item, socket));
    }

    internal static async Task SkillsCommand(ClientState conn, string[] args)
    {
        if (Lab is not { } lab)
        {
            await conn.Commands!.Reply("The game module is not loaded; /skills is unavailable.");
            return;
        }
        string listing = lab.Skills(conn);
        if (args.Length > 0)
        {
            string term = args[0];
            listing = string.Join('\n', listing.Split('\n')
                .Where(line => line.Contains(term, StringComparison.OrdinalIgnoreCase)));
            if (listing.Length == 0) listing = $"No gem matches \"{term}\".";
        }
        await conn.Commands!.Reply(listing);
    }

    /// <summary>/totems [filter]: the living totems of this connection, one line each (id, variety, level, life, cell,
    /// seconds left, the skill each carries), the same style as <c>/skills</c>. A totem is a team-0 monster object kept
    /// apart from <c>ClientState.Monsters</c>, so <c>/mobs</c> and <c>/state</c>'s <c>monsters</c> array cannot show it;
    /// this is how a test answers "was the totem summoned at all?" (Agents/docs/protocol/11-skills.md §10g). Read-only.</summary>
    internal static async Task TotemsCommand(ClientState conn, string[] args)
    {
        string listing = Game.TotemsListing(conn);
        if (args.Length > 0)
        {
            string term = string.Join(' ', args);
            var lines = listing.Split('\n');
            var matched = lines.Where(line => line.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
            listing = matched.Count == 0 ? $"No totem matches \"{term}\"." : string.Join('\n', matched);
        }
        await conn.Commands!.Reply(listing);
    }

    static int CountRecords(ClientState conn, int count, string? filter) =>
        DamagedRecords(conn, count, filter).Count;
}
