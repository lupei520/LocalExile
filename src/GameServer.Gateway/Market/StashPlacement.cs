using GameServer.Core.Protocol.Instance;

/// <summary>Putting an item into a stash tab from outside a connection: a size, a free cell, and the placement itself, for
/// the market's grant, buy and delivery (Agents/docs/dev/market.md).</summary>
static class StashPlacement
{
    /// <summary>An item's grid size and stack, as the game's own shape function reports them.</summary>
    public static PlayerInventories.Shape? ShapeOf(Item item) => Program.Items.ByHash(item.TypeHash) is { } type
        ? new PlayerInventories.Shape(type.Width, type.Height, type.Class, type.MaxStack) : null;

    /// <summary>The first cell of a grid tab where a shape of this size touches no item, or null when the tab is full.</summary>
    public static (int X, int Y)? FreeCell(AccountStash stash, int tab, PlayerInventories.Shape shape)
    {
        if (stash.TabType(tab) is not { } type || !AccountStash.Types.TryGetValue(type, out var grid)) return null;
        var taken = new List<(int X, int Y, int W, int H)>();
        foreach (InventoryItem held in stash.Inventory(tab).Items)
        {
            if (Program.Items.ByHash(held.Item.TypeHash) is not { } heldType) return null;
            taken.Add((held.X, held.Y, heldType.Width, heldType.Height));
        }
        for (int y = 0; y + shape.Height <= grid.Height; y++)
            for (int x = 0; x + shape.Width <= grid.Width; x++)
                if (!taken.Any(t => x < t.X + t.W && t.X < x + shape.Width && y < t.Y + t.H && t.Y < y + shape.Height))
                    return (x, y);
        return null;
    }

    /// <summary>Adds <paramref name="amount"/> of a currency to a tab, in as many cells as its stack limit needs, and
    /// returns how much got there (which is less than the amount only when the tab filled up).</summary>
    public static long PutAmount(AccountStash stash, int tab, ItemCatalog.BaseType coin, long amount, out string why)
    {
        why = "";
        long left = amount, placed = 0;
        int per = Math.Max(1, (int)coin.MaxStack);
        while (left > 0)
        {
            int chunk = (int)Math.Min(left, per);
            var stack = new Item(coin.Hash, coin.Components, coin.VisualIdentity) { StackSize = (ushort)chunk };
            if (PutStackOrMerge(stash, tab, stack, out why) is null) return placed;
            left -= chunk;
            placed += chunk;
        }
        return placed;
    }

    /// <summary>Adds an item to a tab: onto the pile of its own type when there is one with room, else into the first free
    /// cell. Returns what was placed, or null with the reason.</summary>
    public static InventoryItem? PutStackOrMerge(AccountStash stash, int tab, Item item, out string why)
    {
        why = "";
        if (ShapeOf(item) is not { } shape) { why = "unknown item type"; return null; }
        if (shape.MaxStack > 1)
            foreach (InventoryItem pile in stash.Inventory(tab).Items.Where(held => held.Item.TypeHash == item.TypeHash).ToList())
                if (stash.Merge(tab, pile.X, pile.Y, new InventoryItem(pile.Id, pile.X, pile.Y, item), ShapeOf, item.StackSize) is { } merged && merged.Moved == item.StackSize)
                    return merged.Stack;
        if (FreeCell(stash, tab, shape) is not { } cell) { why = "the tab is full"; return null; }
        if (stash.Place(tab, cell.X, cell.Y, new InventoryItem(0, (byte)cell.X, (byte)cell.Y, item), ShapeOf, out why) is not { } put) return null;
        return put.Placed;
    }
}
