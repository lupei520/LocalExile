using GameServer.Core.World;

static partial class Game
{
    /// <summary>
    /// Records one use the ordinary skill path refused before any hit (Agents/docs/dev/combat-lab.md): the same record
    /// type and JSON line as a hit, with <c>Kind "refused"</c>, no damage and <paramref name="reason"/> in
    /// <c>outcome</c>, so "the skill never fired, here is why" is in the ledger instead of only in the Gateway's
    /// console log. <paramref name="skillId"/> 0 means the gem was not known; the record then carries the effect id in
    /// <c>skill</c>.
    /// </summary>
    /// <param name="targetId">The aim when the use had one (the monster's id, its life unchanged), else 0.</param>
    /// <param name="label">The ledger name to use when the caller already knows it (the projectile pump runs where
    /// resolving a gem is not wanted); null looks it up from the effect id and the gem.</param>
    internal static void NoteRefusal(ClientState conn, ushort skillId, int effect, string? gemName, string reason,
        uint targetId = 0, (int X, int Y) aim = default, double distance = 0, string? label = null)
    {
        string? skill = label;
        if (skill is null)
        {
            var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
            string? effectId = SkillsFor(conn).EffectId(effect);
            // The effect id is what the console line carries, and it matches a refused use to its recipe; skillId is the
            // link to the gem's own name in /skills.
            skill = effectId ?? gem.Name ?? gemName ?? (skillId != 0 ? $"0x{skillId:x4}" : "?");
        }
        if (aim != default)
            distance = Dist(conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds), aim);
        // No life moved and nothing is named as the target: a refusal carries the aim (id and distance) but never a
        // monster label, so it can never be read as a hit on one.
        DamageLedger.Note(conn, new DamageRecord(conn.Clock.Elapsed.TotalSeconds, "refused", skill, skillId, targetId,
            "", 0, "", 0, 0, "", false, reason, 0, 0, distance));
    }

    /// <summary>
    /// Records one committed player hit in the lab's ledger (Agents/docs/dev/combat-lab.md). Every site calls it with
    /// the damage value it is about to pass to the apply call — the hit's fully resolved damage: after every
    /// player-side multiplier (critical, impale) and after the target's own resistances and armour — together with the
    /// target's life read immediately before that call and the same source label the hit's own log line uses.
    /// <paramref name="lifeBefore"/> is the target's <c>Life</c> before the hit, so the record's <c>taken</c> is
    /// <c>min(damage, lifeBefore)</c> and <c>lifeAfter</c> always equals <c>lifeBefore - taken</c>: an overkilling hit
    /// has <c>raw &gt; taken</c>, a non-overkilling one has them equal.
    /// </summary>
    internal static void NoteOutgoing(ClientState conn, string skill, ushort skillId, uint id, SpawnedMonster m,
        DamagePacket rolled, int taken, bool critical, uint lifeBefore, double at)
    {
        int applied = (int)Math.Min(Math.Max(0, (long)taken), lifeBefore);
        var (label, level, rarity) = DamageLedger.Describe(m);
        double distance = Dist(conn.Walk.Position(at), m.Walk.Position(at));
        DamageLedger.Note(conn, new DamageRecord(at, "outgoing", skill, skillId, id, label, level, rarity,
            taken, applied, DamageLedger.TypesOf(rolled), critical, m.Life == 0 ? "killed" : "hit",
            lifeBefore, lifeBefore - (uint)applied, distance));
    }

    /// <summary>
    /// Records one committed hit a player totem dealt (<see cref="TotemStrikeLocked"/>), the fifth damage site of the
    /// ledger and the only one no player click produced. Same record and JSON line as <see cref="NoteOutgoing"/>
    /// (<c>Kind "outgoing"</c>; the fight happened, it is just the totem's fight), with the two differences the site's
    /// facts demand: <c>skill</c> is the effect id of the skill the totem carries (or its label), because the totem's
    /// proxy skill has no gem of its own in <c>/skills</c> to name it, and <c>distance</c> runs from the totem to the
    /// monster rather than from the player. The site never rolls a critical for a totem's use, so
    /// <paramref name="critical"/> stays false.
    /// </summary>
    internal static void NoteTotemOutgoing(ClientState conn, TotemState.Totem totem, uint id, SpawnedMonster m,
        DamagePacket rolled, int taken, uint lifeBefore, double at)
    {
        int applied = (int)Math.Min(Math.Max(0, (long)taken), lifeBefore);
        var (label, level, rarity) = DamageLedger.Describe(m);
        DamageLedger.Note(conn, new DamageRecord(at, "outgoing", TotemSkillLabel(conn, totem.Effect), totem.SkillId, id,
            label, level, rarity, taken, applied, DamageLedger.TypesOf(rolled), false,
            m.Life == 0 ? "killed" : "hit", lifeBefore, lifeBefore - (uint)applied,
            Dist(totem.At, m.Walk.Position(at))));
    }

    /// <summary>The id the skill a totem carries goes into the ledger and the /totems line under: the GrantedEffects id
    /// of the row the totem was summoned with (a totem's proxy skill has no gem of its own in /skills to name it), else
    /// its row number. The effect row is stored on the totem rather than looked up among the connection's gems: a totem
    /// outlives the socket it came from, and the gem list is the player's.</summary>
    internal static string TotemSkillLabel(ClientState conn, int effect) =>
        effect < 0 ? "totem" : SkillsFor(conn).EffectId(effect) ?? $"effect {effect}";

    /// <summary>
    /// Records one committed monster hit on the player (Kind "incoming") from the values
    /// <c>CommitMonsterHitLocked</c> just resolved: <paramref name="raw"/> is its RawDamage (the physical component
    /// before armour), <paramref name="taken"/> the damage the hit actually applied, and <paramref name="attack"/>'s
    /// packet the hit's pre-critical composition per type. Overkill applies here too: taken is clamped by
    /// <paramref name="lifeBefore"/>, so lifeAfter == lifeBefore - min(taken, lifeBefore).
    /// </summary>
    internal static void NoteIncoming(ClientState conn, SpawnedMonster m, MonsterAttack attack, int raw, int taken,
        bool blocked, bool critical, uint lifeBefore, double at)
    {
        // No packet crossed: blocked, immune, avoided, or a zero-damage contact. Nothing to compare.
        if (taken <= 0 && raw <= 0) return;
        int applied = (int)Math.Min(Math.Max(0, (long)taken), lifeBefore);
        string target = CharacterName(conn);
        DamageLedger.Note(conn, new DamageRecord(at, "incoming", m.Label, attack.SkillId, PlayerId, target,
            conn.Pools.Level, "", raw, applied, DamageLedger.TypesOf(attack.Damage), critical,
            conn.PlayerLife == 0 ? "killed" : blocked ? "blocked" : "hit", lifeBefore,
            lifeBefore - (uint)applied, 0));
    }
}
