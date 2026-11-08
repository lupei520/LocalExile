using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Buying a listed item with nobody in game (Agents/docs/dev/market.md): the buyer pays the listing's price from their own
/// stash, the item moves out of the seller's tab into the buyer's market tab, and the seller's proceeds go into theirs.
/// This is what the game's asynchronous trade does; the page stands in for the NPC's window.
/// </summary>
/// <remarks>
/// Everything happens under one lock in this process, so two buyers cannot both take the same item and a seller cannot be
/// paid twice: the listing is re-read inside the lock and the item is taken before anything is paid. A listing is bought
/// whole, exactly as it sits in the tab. Gold is the fee, as Faustus charges, scaled by the size of the trade
/// (<c>Game_MARKET_FEE_BPS</c>, hundredths of a percent: 500 = 5%), and a step that fails puts back what the earlier ones
/// moved before the request is refused.
/// </remarks>
static class MarketTrade
{
    /// <summary>One trade at a time: the stashes are shared and a half-done trade must not be visible to another.</summary>
    static readonly object Gate = new();

    /// <summary>Buys a listing as JSON. <paramref name="status"/> is 200 or the reason's status.</summary>
    public static string Buy(string buyer, string seller, string? league, int tab, uint itemId, out int status)
    {
        status = 200;
        if (string.IsNullOrWhiteSpace(buyer)) { status = 403; return Fail("sign in first"); }
        if (string.IsNullOrWhiteSpace(seller)) { status = 400; return Fail("seller is required"); }
        if (string.Equals(buyer, seller, StringComparison.OrdinalIgnoreCase)) { status = 400; return Fail("you cannot buy your own listing"); }

        lock (Gate)
        {
            AccountStash sellerStash, buyerStash;
            try
            {
                sellerStash = AccountStash.For(AccountStash.Key(seller, league), Program.Items);
                buyerStash = AccountStash.For(AccountStash.Key(buyer, league), Program.Items);
            }
            catch (Exception e) when (e is InvalidOperationException or System.Text.Json.JsonException)
            {
                status = 400;
                return Fail($"that stash cannot be read: {e.Message}");
            }

            if (!sellerStash.IsPublic(tab)) { status = 400; return Fail("that tab is no longer public"); }
            if (sellerStash.Inventory(tab).Items.FirstOrDefault(held => held.Id == itemId) is not { } listed)
            { status = 404; return Fail("that item is no longer in the tab"); }
            if (MarketIndex.PriceOf(sellerStash, tab, listed) is not { } price) { status = 400; return Fail("that item has no price note"); }
            if (price.Kind is not ("price" or "b/o")) { status = 400; return Fail($"that listing is {price.Kind}: whisper the seller"); }
            if (MarketNote.CurrencyType(Program.Items, price.Currency) is not { } coin) { status = 400; return Fail($"unknown currency \"{price.Currency}\""); }

            var coins = Coins(buyerStash, coin.Hash);
            long have = coins.Sum(one => (long)one.Item.Item.StackSize);
            if (have < price.Amount) { status = 400; return Fail($"the buyer has {have} {coin.Name}, the price is {price.Amount}"); }
            long fee = Fee(price);
            if (buyerStash.Gold < fee) { status = 400; return Fail($"the fee is {fee} gold and the buyer has {buyerStash.Gold}"); }

            // The trade itself. Each step can only fail on something the checks above cannot see (a full tab), and the ones
            // already done are put back before the refusal.
            int buyerTab = buyerStash.EnsureTab(MonitorGrant.MarketTab), sellerTab = sellerStash.EnsureTab(MonitorGrant.MarketTab);
            if (sellerStash.Take(tab, itemId) is not { } bought) { status = 404; return Fail("that item is no longer in the tab"); }
            if (StashPlacement.PutStackOrMerge(buyerStash, buyerTab, bought.Item, out _) is not { } delivered)
            {
                StashPlacement.PutStackOrMerge(sellerStash, tab, bought.Item, out _);
                status = 400;
                return Fail($"the buyer's \"{MonitorGrant.MarketTab}\" tab has no room");
            }
            Pay(buyerStash, coins, price.Amount);

            if (buyerStash.TrySpendGold(fee) is null)
            {
                Undo(buyerStash, buyerTab, delivered, coin, price.Amount);
                status = 400;
                return Fail($"the fee is {fee} gold and the buyer has {buyerStash.Gold}");
            }
            if (StashPlacement.PutAmount(sellerStash, sellerTab, coin, price.Amount, out string why) < price.Amount)
            {
                buyerStash.AddGold(fee);
                Undo(buyerStash, buyerTab, delivered, coin, price.Amount);
                status = 400;
                return Fail($"the seller's \"{MonitorGrant.MarketTab}\" tab has no room: {why}");
            }

            return new JsonObject
            {
                ["ok"] = true,
                ["buyer"] = buyer,
                ["seller"] = seller,
                ["item"] = Program.Items.ByHash(bought.Item.TypeHash)?.Name ?? "?",
                ["price"] = price.Text,
                ["fee"] = fee,
                ["goldLeft"] = buyerStash.Gold,
            }.ToJsonString();
        }
    }

    /// <summary>Takes <paramref name="amount"/> from the buyer's stacks, putting back what a partly used stack leaves.</summary>
    static void Pay(AccountStash buyerStash, List<(int Tab, InventoryItem Item)> coins, long amount)
    {
        long left = amount;
        foreach ((int tab, InventoryItem stack) in coins)
        {
            if (left <= 0) break;
            int use = (int)Math.Min(left, stack.Item.StackSize);
            if (buyerStash.Take(tab, stack.Id) is not { } whole) continue;
            left -= use;
            if (use < whole.Item.StackSize)
                StashPlacement.PutStackOrMerge(buyerStash, tab, whole.Item with { StackSize = (ushort)(whole.Item.StackSize - use) }, out _);
        }
    }

    /// <summary>A failed trade: the item goes back to the buyer's tab as it was, and the coins it paid come back.</summary>
    static void Undo(AccountStash buyerStash, int buyerTab, InventoryItem delivered, ItemCatalog.BaseType coin, long amount)
    {
        if (buyerStash.Take(buyerTab, delivered.Id) is { } back) StashPlacement.PutStackOrMerge(buyerStash, buyerTab, back.Item, out _);
        StashPlacement.PutAmount(buyerStash, buyerTab, coin, amount, out _);
    }

    /// <summary>The gold Faustus would charge for this trade: a share of the price, at least one gold.</summary>
    static long Fee(MarketNote.Price price)
    {
        int bps = int.TryParse(Environment.GetEnvironmentVariable("Game_MARKET_FEE_BPS"), out int set) && set >= 0 ? set : 500;
        return price.Amount <= 0 ? 0 : Math.Max(1, (price.Amount * bps + 9999) / 10000);
    }

    /// <summary>The buyer's stacks of one currency, in tab order.</summary>
    static List<(int Tab, InventoryItem Item)> Coins(AccountStash stash, uint typeHash)
    {
        var coins = new List<(int Tab, InventoryItem Item)>();
        for (int tab = 0; tab < stash.Count; tab++)
            foreach (InventoryItem held in stash.Inventory(tab).Items)
                if (held.Item.TypeHash == typeHash) coins.Add((tab, held));
        return coins;
    }

    static string Fail(string reason) => new JsonObject { ["ok"] = false, ["error"] = reason }.ToJsonString();
}
