using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core.Diagnostics;
using GameServer.Core.World;

/// <summary>
/// One resolved hit, as the in-game lab records it: what the skill dealt and what the target actually lost, with what
/// a damage formula needs to be recomputed and compared (Agents/docs/dev/combat-lab.md).
/// <see cref="Raw"/> is the packet's <em>physical</em> component after armour, which is what the incoming path measures
/// (<c>taken[Physical].Min</c>): it is not the whole hit, so a pure elemental or chaos hit records <c>Raw = 0</c> and a
/// comparison script that treats it as the resolved damage is wrong. <see cref="Types"/> is the rolled packet's whole
/// pre-critical composition per type, which is where the other types are. <see cref="Taken"/> is the life the target
/// actually lost, clamped by <see cref="LifeBefore"/>; a hit that overkills has <c>Taken = LifeBefore</c>.
/// </summary>
internal sealed record DamageRecord(double At, string Kind, string Skill, ushort SkillId, uint TargetId,
    string Target, int TargetLevel, string TargetRarity, int Raw, int Taken, string Types, bool Critical,
    string Outcome, uint LifeBefore, uint LifeAfter, double Distance);

/// <summary>
/// The lab's damage ledger: the last <see cref="Capacity"/> records of one connection, so an agent or a script can read
/// what a skill actually did instead of subtracting two life readings by hand. A committed hit is recorded after the
/// damage was applied; a use the ordinary path refused before any hit is recorded as the same record with
/// <c>Kind "refused"</c>, no damage and the reason in <c>outcome</c> (Agents/docs/dev/combat-lab.md), so "the skill
/// never fired, here is why" is evidence rather than a missing row. <see cref="Note"/> never changes a fight.
/// <c>Game_TRACE=damage</c> also writes every record to the Gateway log as one <c>[damage] {json}</c> line.
/// </summary>
internal static class DamageLedger
{
    internal const int Capacity = 256;

    static readonly ConditionalWeakTable<ClientState, List<DamageRecord>> Buffers = new();
    static readonly Lock Gate = new();

    /// <summary>Records one committed hit. Call it after the damage has been applied, with the target's life before it.</summary>
    internal static void Note(ClientState conn, DamageRecord record)
    {
        lock (Gate)
        {
            List<DamageRecord> list = Buffers.GetOrCreateValue(conn);
            list.Add(record);
            if (list.Count > Capacity) list.RemoveRange(0, list.Count - Capacity);
        }
        if (ServerTrace.Enabled("damage")) Console.WriteLine($"[damage] {Json(record)}");
    }

    /// <summary>The newest <paramref name="count"/> records, oldest first. Empty when nothing was recorded.</summary>
    internal static IReadOnlyList<DamageRecord> Recent(ClientState conn, int count)
    {
        lock (Gate)
        {
            if (!Buffers.TryGetValue(conn, out var list)) return [];
            int take = Math.Clamp(count, 1, Capacity);
            return take >= list.Count ? [.. list] : [.. list.Skip(list.Count - take)];
        }
    }

    internal static void Clear(ClientState conn)
    {
        lock (Gate)
            if (Buffers.TryGetValue(conn, out var list)) list.Clear();
    }

    /// <summary>One record as a compact single-line JSON object: the machine-readable form of the ledger.</summary>
    internal static string Json(DamageRecord r) => JsonSerializer.Serialize(new
    {
        at = Math.Round(r.At, 4), kind = r.Kind, skill = r.Skill, skillId = r.SkillId, targetId = r.TargetId,
        target = r.Target, level = r.TargetLevel, rarity = r.TargetRarity, raw = r.Raw, taken = r.Taken,
        types = r.Types, crit = r.Critical, outcome = r.Outcome, lifeBefore = r.LifeBefore, lifeAfter = r.LifeAfter,
        distance = Math.Round(r.Distance, 2),
    });

    /// <summary>The rolled packet per type as <c>"physical:12,cold:30"</c>: what a hit is made of before mitigation.</summary>
    internal static string TypesOf(DamagePacket packet)
    {
        var parts = new List<string>(5);
        foreach (DamageType type in Enum.GetValues<DamageType>())
        {
            double value = packet[type].Max;
            if (value > 0) parts.Add($"{type.ToString().ToLowerInvariant()}:{value:0.##}");
        }
        return parts.Count == 0 ? "" : string.Join(',', parts);
    }

    /// <summary>A monster's identity for a record: its label, its level and its rarity.</summary>
    internal static (string Label, int Level, string Rarity) Describe(SpawnedMonster m) =>
        (m.Label, m.Level, m.Rarity.ToString().ToLowerInvariant());
}
