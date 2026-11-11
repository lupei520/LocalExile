using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    static readonly (string Name, string Stat, int Value)[] JourneyTattoos =
    [
        ("Journey Tattoo of the Body", "base_maximum_life", 25),
        ("Journey Tattoo of the Mind", "base_maximum_mana", 30),
        ("Journey Tattoo of the Soul", "base_maximum_energy_shield", 40),
    ];

    /// <summary>The counting tattoos' aggregate stats: Journey Body/Mind/Soul grant their flat pool once per allocated
    /// copy, and Ancestral Bloodlines grants recovery per distinct tribe among the allocated tattoos. The marker
    /// stats on each node are ignored by consumers; this pass emits the real stat keys once per character. A radius
    /// jewel's tattoo effect scales the pool each copy grants, so the copies are summed first and the copy count is
    /// never multiplied in again.</summary>
    public static Dictionary<uint, int> TattooCountingStats(IReadOnlyDictionary<ushort, string> tattoos,
        IReadOnlySet<ushort> allocated, IReadOnlyDictionary<ushort, int>? tattooEffect = null)
    {
        var result = new Dictionary<uint, int>();
        // Every copy of a counting tattoo grants its own flat pool, each scaled by the radius jewels in its own radius.
        // The sum below is already the total, so the callers must not multiply it by the copy count again: two Journey
        // Body tattoos without a radius jewel are 25 + 25, not two times (25 + 25).
        int Total(string name, int value) =>
            tattoos.Where(kv => allocated.Contains(kv.Key) && kv.Value == name)
                .Sum(kv => RadiusJewelCatalogue.ScaleEffect(value, tattooEffect?.GetValueOrDefault(kv.Key) ?? 0));
        void Add(string stat, int value)
        {
            if (Stats.Key(stat) is uint key) result[key] = result.GetValueOrDefault(key) + value;
        }
        foreach (var (name, stat, value) in JourneyTattoos)
        {
            int total = Total(name, value);
            if (total > 0) Add(stat, total);
        }
        bool ancestral = tattoos.Any(kv => allocated.Contains(kv.Key)
            && kv.Value == "Ancestral Tattoo of Bloodlines");
        int tribes = ancestral
            ? tattoos
                .Where(kv => allocated.Contains(kv.Key))
                .Select(kv => TattooCatalogue.Find(kv.Value)?.Tribe ?? 11)
                .Where(tribe => tribe is >= 0 and <= 10)
                .Distinct()
                .Count()
            : 0;
        if (tribes > 0)
        {
            // The recovery is one flat pool per distinct tribe, scaled by the radius jewels in the ancestral copies'
            // own radii. Their effects scale that pool once, so the number of ancestral copies does not multiply it
            // a second time: with none in a radius the result is the flat 2 the base rule grants.
            int effect = tattoos.Where(kv => allocated.Contains(kv.Key)
                && kv.Value == "Ancestral Tattoo of Bloodlines")
                .Sum(kv => tattooEffect?.GetValueOrDefault(kv.Key) ?? 0);
            int recovery = tribes * RadiusJewelCatalogue.ScaleEffect(2, effect);
            Add("life_recovery_rate_+%", recovery);
            Add("mana_recovery_rate_+%", recovery);
            Add("energy_shield_recovery_rate_+%", recovery);
        }
        return result;
    }
}
