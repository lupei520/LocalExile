using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The currency exchange (Agents/docs/dev/market.md): Faustus's order book, on the page instead of in the client's
/// window. A player says what they give and what they want; the book matches it against the other orders, fills what it
/// can (an order may be filled in part), takes a fee in gold, and both sides are paid into their market tabs. Nobody has
/// to be in game or even online.
/// </summary>
/// <remarks>
/// The currency a player offers leaves their stash when the order is placed, exactly as Faustus takes it, and waits in the
/// order until it is filled or cancelled — so an order can always be honoured. Orders are kept per account in that
/// account's saved module data (<see cref="AccountStash.ChangeModuleData"/>, key <see cref="Key"/>), so a restart keeps
/// them. Matching runs under one lock: a pair must be exactly opposite (A gives X, wants Y; B gives Y, wants X) and the
/// new order's rate must be at least as good as the resting one's; the fill uses the resting order's rate.
/// </remarks>
static class MarketExchange
{
    /// <summary>Where an account's open orders are saved, inside its stash.</summary>
    public const string Key = "market.exchange";

    /// <summary>An order: what it gives and wants, in whole units of a stackable item, and how much of each is done. The
    /// filled counters move as the book matches, so this is the one mutable thing here (under <see cref="Gate"/>).</summary>
    public sealed class Order
    {
        public long Id { get; init; }
        public string Account { get; set; } = "";
        public string? League { get; set; }
        public uint GiveHash { get; init; }
        public int GiveAmount { get; init; }
        public uint WantHash { get; init; }
        public int WantAmount { get; init; }
        public int FilledGive { get; set; }
        public int FilledWant { get; set; }
        public long PlacedAt { get; init; }

        public int LeftGive => GiveAmount - FilledGive;
        public int LeftWant => WantAmount - FilledWant;
        public bool Done => LeftGive <= 0;
        /// <summary>What one unit of the wanted item costs in the given one.</summary>
        public double Rate => WantAmount <= 0 ? 0 : (double)GiveAmount / WantAmount;
    }

    static readonly object Gate = new();
    static readonly List<Order> Book = [];
    static long NextId = 1;
    static bool Seeded;

    static readonly JsonSerializerOptions Orders = new(JsonSerializerDefaults.General);

    /// <summary>The book, and the signed-in account's own orders. <paramref name="account"/> may be null.</summary>
    public static string View(string? account)
    {
        lock (Gate)
        {
            SeedLocked();
            var orders = new JsonArray();
            foreach (Order order in Book.OrderBy(order => order.GiveHash).ThenBy(order => order.WantHash).ThenBy(order => order.Id))
                orders.Add(View(order));
            var mine = new JsonArray();
            if (account is { Length: > 0 })
                foreach (Order order in Book.Where(order => string.Equals(order.Account, account, StringComparison.OrdinalIgnoreCase)))
                    mine.Add(View(order));
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Order order in Book)
            {
                if (Name(order.GiveHash) is { Length: > 0 } give) names.Add(give);
                if (Name(order.WantHash) is { Length: > 0 } want) names.Add(want);
            }
            foreach (string common in new[] { "Chaos Orb", "Divine Orb", "Exalted Orb", "Orb of Alchemy", "Orb of Fusing" }) names.Add(common);
            return new JsonObject
            {
                ["count"] = Book.Count,
                ["feeBps"] = FeeBps,
                ["orders"] = orders,
                ["mine"] = mine,
                ["currencies"] = new JsonArray([.. names.Select(name => (JsonNode)name)]),
            }.ToJsonString();
        }
    }

    /// <summary>Places an order, taking what it offers out of the player's stash, and matches it.</summary>
    public static string Place(string account, string? league, string give, int giveAmount, string want, int wantAmount, out int status)
    {
        status = 200;
        if (string.IsNullOrWhiteSpace(account)) { status = 403; return Fail("sign in first"); }
        if (giveAmount <= 0 || wantAmount <= 0) { status = 400; return Fail("both amounts must be more than zero"); }
        if (string.Equals(give, want, StringComparison.OrdinalIgnoreCase)) { status = 400; return Fail("giving and wanting the same currency is not an exchange"); }

        lock (Gate)
        {
            SeedLocked();
            if (MarketNote.CurrencyType(Program.Items, give) is not { } giveType) { status = 400; return Fail($"unknown currency \"{give}\""); }
            if (MarketNote.CurrencyType(Program.Items, want) is not { } wantType) { status = 400; return Fail($"unknown currency \"{want}\""); }
            if (!giveType.Stackable || !wantType.Stackable) { status = 400; return Fail("both sides must be a stackable currency"); }

            AccountStash stash;
            try
            {
                stash = AccountStash.For(AccountStash.Key(account, league), Program.Items);
            }
            catch (Exception e) when (e is InvalidOperationException or JsonException)
            {
                status = 400;
                return Fail($"that stash cannot be read: {e.Message}");
            }
            if (Take(stash, giveType, giveAmount) is not { } held)
            {
                status = 400;
                return Fail($"the stash holds {Held(stash, giveType.Hash)} {giveType.Name}, the order gives {giveAmount}");
            }

            var order = new Order
            {
                Id = NextId++,
                Account = account,
                League = league,
                GiveHash = giveType.Hash,
                GiveAmount = giveAmount,
                WantHash = wantType.Hash,
                WantAmount = wantAmount,
                PlacedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            Book.Add(order);
            Save(account, league);
            var fills = Match(order, out string? note);
            return new JsonObject
            {
                ["ok"] = true,
                ["id"] = order.Id,
                ["give"] = giveType.Name,
                ["giveAmount"] = giveAmount,
                ["want"] = wantType.Name,
                ["wantAmount"] = wantAmount,
                ["filled"] = fills,
                ["left"] = new JsonObject { ["give"] = order.LeftGive, ["want"] = order.LeftWant },
                ["note"] = note ?? "",
                ["held"] = new JsonArray([.. held.Select(one => $"{one.Amount} {giveType.Name}")]),
            }.ToJsonString();
        }
    }

    /// <summary>Cancels an order and returns what it still holds to the player's market tab.</summary>
    public static string Cancel(string account, long id, out int status)
    {
        status = 200;
        if (string.IsNullOrWhiteSpace(account)) { status = 403; return Fail("sign in first"); }
        lock (Gate)
        {
            SeedLocked();
            if (Book.FirstOrDefault(order => order.Id == id && string.Equals(order.Account, account, StringComparison.OrdinalIgnoreCase)) is not { } order)
            { status = 404; return Fail("no such order of yours"); }
            var stash = AccountStash.For(AccountStash.Key(order.Account, order.League), Program.Items);
            int tab = stash.EnsureTab(MonitorGrant.MarketTab);
            string gaveBack = "nothing left";
            if (order.LeftGive > 0 && Program.Items.ByHash(order.GiveHash) is { } giveType)
            {
                if (StashPlacement.PutAmount(stash, tab, giveType, order.LeftGive, out string why) < order.LeftGive)
                {
                    status = 400;
                    return Fail($"the \"{MonitorGrant.MarketTab}\" tab has no room for what is left: {why}");
                }
                gaveBack = $"{order.LeftGive} {giveType.Name}";
            }
            Book.Remove(order);
            Save(order.Account, order.League);
            return new JsonObject { ["ok"] = true, ["id"] = id, ["returned"] = gaveBack }.ToJsonString();
        }
    }

    /// <summary>Matches one order against the resting book, filling what it can and paying both sides.</summary>
    static JsonArray Match(Order placed, out string? note)
    {
        note = null;
        var fills = new JsonArray();
        foreach (Order resting in Book.Where(order => !ReferenceEquals(order, placed) && !order.Done).OrderBy(order => order.PlacedAt).ToList())
        {
            if (placed.Done) break;
            // Exactly opposite pair: the rest of the book gives what this order wants and wants what it gives.
            if (resting.Account.Equals(placed.Account, StringComparison.OrdinalIgnoreCase)) continue;
            if (resting.GiveHash != placed.WantHash || resting.WantHash != placed.GiveHash) continue;
            // The resting order trades `resting.GiveAmount` of its currency for `resting.WantAmount` of the new
            // order's: so it asks resting.GiveAmount per resting.WantAmount, and the new order must give at least as
            // much per unit as that (cross-multiplied, so the comparison is exact).
            if (resting.WantAmount <= 0 || resting.GiveAmount <= 0) continue;
            if ((long)placed.WantAmount * resting.WantAmount < (long)resting.GiveAmount * placed.GiveAmount) continue;
            long gives = Math.Min(Math.Min(resting.LeftGive, placed.LeftWant),
                (long)placed.LeftGive * resting.GiveAmount / resting.WantAmount);
            if (gives <= 0) continue;
            long wants = gives * resting.WantAmount / resting.GiveAmount;
            if (wants <= 0) continue;

            if (Program.Items.ByHash(resting.GiveHash) is not { } giveType || Program.Items.ByHash(resting.WantHash) is not { } wantType) continue;
            var placedStash = AccountStash.For(AccountStash.Key(placed.Account, placed.League), Program.Items);
            var restingStash = AccountStash.For(AccountStash.Key(resting.Account, resting.League), Program.Items);
            int placedTab = placedStash.EnsureTab(MonitorGrant.MarketTab), restingTab = restingStash.EnsureTab(MonitorGrant.MarketTab);
            // Each side pays for what it receives: the placed order receives `gives`, the resting one receives `wants`.
            long placedFee = Fee(gives), restingFee = Fee(wants);
            if (placedStash.Gold < placedFee || restingStash.Gold < restingFee) { note = "a side could not pay the gold fee"; continue; }

            // Pay both sides first; anything that cannot be delivered leaves the fill undone.
            // The placed order receives what the resting one gives (gives), and the resting one receives `wants` of the
            // other currency. Anything that cannot be delivered in full leaves the fill undone.
            if (StashPlacement.PutAmount(placedStash, placedTab, giveType, gives, out string why) < gives)
            { note = $"the placed order's tab: {why}"; continue; }
            if (StashPlacement.PutAmount(restingStash, restingTab, wantType, wants, out why) < wants)
            {
                Undeliver(placedStash, placedTab, giveType, gives);
                note = $"the resting order's tab: {why}";
                continue;
            }
            placedStash.TrySpendGold(placedFee);
            restingStash.TrySpendGold(restingFee);

            placed.FilledGive += (int)wants; placed.FilledWant += (int)gives;
            resting.FilledGive += (int)gives; resting.FilledWant += (int)wants;
            if (resting.Done) { Book.Remove(resting); Save(resting.Account, resting.League); }
            Save(placed.Account, placed.League);
            Save(resting.Account, resting.League);
            fills.Add(new JsonObject
            {
                ["with"] = resting.Account,
                ["gave"] = $"{wants} {wantType.Name}",
                ["got"] = $"{gives} {giveType.Name}",
                ["fee"] = placedFee,
            });
        }
        if (placed.Done) { Book.Remove(placed); Save(placed.Account, placed.League); }
        return fills;
    }

    /// <summary>Reads every account's saved orders once, so the book is the same after a restart.</summary>
    static void SeedLocked()
    {
        if (Seeded) return;
        Seeded = true;
        foreach ((string account, string? league, AccountStash stash) in MarketIndex.Stashes())
        {
            if (stash.ModuleData(Key) is not { Length: > 0 } saved) continue;
            try
            {
                foreach (Order? order in JsonSerializer.Deserialize<List<Order>>(saved, Orders) ?? [])
                    if (order is { } open && open.LeftGive > 0)
                    {
                        // The stash it was found in is what the order trades for: a league's stash trades in that league.
                        open.Account = account;
                        open.League = league;
                        Book.Add(open);
                        NextId = Math.Max(NextId, open.Id + 1);
                    }
            }
            catch (JsonException)
            {
                // A saved book this build cannot read: that account's orders are left out, the rest still trade.
            }
        }
    }

    /// <summary>Saves one account's open orders into its own stash.</summary>
    static void Save(string account, string? league)
    {
        try
        {
            var stash = AccountStash.For(AccountStash.Key(account, league), Program.Items);
            var mine = Book.Where(order => string.Equals(order.Account, account, StringComparison.OrdinalIgnoreCase)
                && string.Equals(order.League ?? "", league ?? "", StringComparison.OrdinalIgnoreCase)).ToList();
            stash.ChangeModuleData(Key, _ => mine.Count == 0 ? "" : JsonSerializer.Serialize(mine, Orders));
        }
        catch (Exception e) when (e is InvalidOperationException or JsonException)
        {
            Console.WriteLine($"[market] {account}: the exchange could not be saved: {e.Message}");
        }
    }

    /// <summary>How much of one currency a stash holds, across all its tabs.</summary>
    static long Held(AccountStash stash, uint typeHash)
    {
        long have = 0;
        for (int tab = 0; tab < stash.Count; tab++)
            foreach (InventoryItem held in stash.Inventory(tab).Items)
                if (held.Item.TypeHash == typeHash) have += held.Item.StackSize;
        return have;
    }

    /// <summary>Takes a whole amount of one currency out of a stash, putting back what a partly used stack leaves.
    /// Returns the parts it took, or null when the stash does not hold that much.</summary>
    static List<(int Tab, int Amount)>? Take(AccountStash stash, ItemCatalog.BaseType coin, int amount)
    {
        var stacks = new List<(int Tab, InventoryItem Item)>();
        for (int tab = 0; tab < stash.Count; tab++)
            foreach (InventoryItem held in stash.Inventory(tab).Items)
                if (held.Item.TypeHash == coin.Hash) stacks.Add((tab, held));
        if (stacks.Sum(one => (long)one.Item.Item.StackSize) < amount) return null;
        var taken = new List<(int Tab, int Amount)>();
        int left = amount;
        foreach ((int tab, InventoryItem stack) in stacks)
        {
            if (left <= 0) break;
            int use = Math.Min(left, stack.Item.StackSize);
            if (stash.Take(tab, stack.Id) is not { } whole) continue;
            left -= use;
            taken.Add((tab, use));
            if (use < whole.Item.StackSize)
                StashPlacement.PutStackOrMerge(stash, tab, whole.Item with { StackSize = (ushort)(whole.Item.StackSize - use) }, out _);
        }
        return left == 0 ? taken : null;
    }

    /// <summary>Takes back what a fill had already delivered when its other half could not be delivered.</summary>
    static void Undeliver(AccountStash stash, int tab, ItemCatalog.BaseType coin, long amount)
    {
        long left = amount;
        foreach (InventoryItem held in stash.Inventory(tab).Items.Where(item => item.Item.TypeHash == coin.Hash).ToList())
        {
            if (left <= 0) break;
            int use = (int)Math.Min(left, held.Item.StackSize);
            if (stash.Take(tab, held.Id) is not { } whole) continue;
            left -= use;
            if (use < whole.Item.StackSize)
                StashPlacement.PutStackOrMerge(stash, tab, whole.Item with { StackSize = (ushort)(whole.Item.StackSize - use) }, out _);
        }
    }

    static JsonObject View(Order order) => new()
    {
        ["id"] = order.Id,
        ["account"] = order.Account,
        ["league"] = order.League ?? "Standard",
        ["give"] = Name(order.GiveHash),
        ["giveLeft"] = order.LeftGive,
        ["want"] = Name(order.WantHash),
        ["wantLeft"] = order.LeftWant,
        ["rate"] = $"{order.GiveAmount} {Name(order.GiveHash)} ← {order.WantAmount} {Name(order.WantHash)}",
        ["placed"] = order.PlacedAt,
    };

    static string Name(uint hash) => Program.Items.ByHash(hash)?.Name ?? "?";

    /// <summary>The gold fee for receiving this many units, as Faustus charges (hundredths of a percent).</summary>
    static long Fee(long units)
    {
        long fee = (units * FeeBps + 9999) / 10000;
        return Math.Max(1, fee);
    }

    static int FeeBps => int.TryParse(Environment.GetEnvironmentVariable("Game_MARKET_FEE_BPS"), out int set) && set >= 0 ? set : 500;

    static string Fail(string reason) => new JsonObject { ["ok"] = false, ["error"] = reason }.ToJsonString();
}
