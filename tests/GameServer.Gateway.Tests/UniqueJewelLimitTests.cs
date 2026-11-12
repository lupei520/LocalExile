using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class UniqueJewelLimitTests
{
    [Fact]
    public void The_clients_own_limit_stops_a_second_copy_of_a_unique_jewel_from_being_socketed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jewel-limit-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        var items = Program.Items;
        var conn = new ClientState(new(1, "limit", 100, 100, 100, 100, null, []), 1, 1) { Character = "JewelLimit", ClassRow = 1 };
        conn.Pools = Game.PoolsFor(conn, 40);
        try
        {
            // The client limits this unique to one socketed copy (its UniqueJewelLimits table).
            var jewel = new UniqueCatalog(items).Create("Hidden Potential", new Random(7));
            Assert.NotNull(jewel);
            Assert.Equal(ItemRarity.Unique, jewel!.Rarity);
            var slots = Enumerable.Range(0, 60).Where(s => Program.Passives.JewelNodeForSlot(s) is not null).Take(2).ToList();
            Assert.Equal(2, slots.Count);
            conn.Passives.Add(Program.Passives.JewelNodeForSlot(slots[0])!.Value);
            conn.Passives.Add(Program.Passives.JewelNodeForSlot(slots[1])!.Value);
            Assert.Null(Game.PassiveJewelRefusal(conn, slots[0], 0, jewel));
            Game.ItemsOf(conn).Put(PlayerInventories.PassiveJewels, new(99, (byte)slots[0], 0, jewel));
            Assert.NotNull(Game.PassiveJewelRefusal(conn, slots[1], 0, jewel));
        }
        finally { PlayerInventories.Forget(conn.Character!); TestEnvironment.ReleaseSaves(path); }
    }

    [Fact]
    public void The_client_names_the_limit_of_the_unique_jewels_it_limits()
    {
        var items = Program.Items;
        var uniques = new UniqueCatalog(items);
        // The client's own table: one copy of most unique jewels, two of the threshold jewels, three of the Grand
        // Spectrum.
        Assert.Equal(1, uniques.JewelLimit(uniques.Create("Hidden Potential", new Random(1))!.NameWords[0])?.Limit);
        Assert.Equal(2, uniques.JewelLimit(uniques.Create("Collateral Damage", new Random(1))!.NameWords[0])?.Limit);
        Assert.Equal(3, uniques.JewelLimit(uniques.Create("Grand Spectrum", new Random(1))!.NameWords[0])?.Limit);
    }
}
