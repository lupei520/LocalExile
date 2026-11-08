/// <summary>
/// A price note, as Path of Exile's trade site reads it (Agents/docs/dev/market.md). The server keeps the note as the
/// player wrote it; this is the reading of it that decides whether an item is listed at all and at what price.
/// </summary>
/// <remarks>
/// The grammar is the game's own: <c>~price 5 chaos</c> (a fixed price), <c>~b/o 2 divine</c> (buyout), <c>~offer</c>
/// (offers are welcome), <c>~free</c>, and the bulk form <c>~price 200/1 chaos/divine</c> (200 chaos wanted, 1 divine
/// given). Anything without the leading <c>~</c> is not a listing, which is how the game hides an item from the site.
/// </remarks>
static class MarketNote
{
    /// <summary>What a note says: <paramref name="Kind"/> is <c>price</c>, <c>b/o</c>, <c>offer</c>, <c>free</c> or
    /// <c>bulk</c>; <paramref name="Amount"/> of <paramref name="Currency"/> is what the seller wants (a bulk note also
    /// says what they give: <paramref name="Give"/> of <paramref name="GiveCurrency"/>).</summary>
    public sealed record Price(string Kind, long Amount, string Currency, long Give, string GiveCurrency, string Raw)
    {
        /// <summary>How the page prints it.</summary>
        public string Text => Kind switch
        {
            "offer" => "接受出价",
            "free" => "免费",
            "bulk" => $"{Give} {GiveCurrency} → {Amount} {Currency}",
            _ => $"{Amount} {Currency}",
        };
    }

    /// <summary>The price a note states, or null when the note is not a listing.</summary>
    public static Price? Parse(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;
        string text = note.Trim();
        if (text.Length < 2 || text[0] != '~') return null;
        string[] parts = text[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;
        switch (parts[0].ToLowerInvariant())
        {
            case "free":
                return new Price("free", 0, "", 0, "", text);
            case "offer":
            case "ask":
                return new Price("offer", 0, "", 0, "", text);
            case "price":
            case "b/o":
            case "bo":
                break;
            default:
                return null;
        }
        string kind = parts[0].ToLowerInvariant() is "b/o" or "bo" ? "b/o" : "price";
        if (parts.Length < 3) return null;

        // "5 chaos": a price in one currency.
        if (!parts[1].Contains('/') && long.TryParse(parts[1], out long amount) && amount > 0)
            return new Price(kind, amount, string.Join(' ', parts[2..]), 0, "", text);

        // "200/1 chaos/divine": what the seller gives, then what they want.
        if (parts.Length == 3 && parts[1].Split('/') is { Length: 2 } ratio && parts[2].Split('/') is { Length: 2 } money
            && long.TryParse(ratio[0], out long give) && long.TryParse(ratio[1], out long want) && give > 0 && want > 0)
            return new Price("bulk", want, money[1], give, money[0], text);
        return null;
    }

    /// <summary>The currency a note names ("chaos", "divine", "alch" …) as a base type, or null when nothing matches. The
    /// game's own keywords are short where the item names are not, so the aliases are spelled out and anything else is
    /// matched against the stackable rows by name.</summary>
    public static ItemCatalog.BaseType? CurrencyType(ItemCatalog catalog, string word)
    {
        word = word.Trim();
        if (word.Length == 0) return null;
        // The aliases first, then the shortest stackable row whose name begins with the word: a loose "contains" would
        // take "chaos" for Veiled Chaos Orb.
        if (Aliases.TryGetValue(word, out string? name) && catalog.Find(name) is { } alias) return alias;
        var stackable = catalog.All.Where(row => row.Class == "StackableCurrency" && row.MaxStack > 1 && row.Name.Length > 0).ToList();
        return stackable.Where(row => row.Name.StartsWith(word, StringComparison.OrdinalIgnoreCase)).OrderBy(row => row.Name.Length).FirstOrDefault()
            ?? stackable.Where(row => string.Equals(row.Name, word, StringComparison.OrdinalIgnoreCase)).FirstOrDefault()
            ?? stackable.Where(row => row.Name.Contains(" " + word, StringComparison.OrdinalIgnoreCase)).OrderBy(row => row.Name.Length).FirstOrDefault();
    }

    /// <summary>The words players write where the item's name is longer.</summary>
    static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chaos"] = "Chaos Orb", ["divine"] = "Divine Orb", ["exalt"] = "Exalted Orb", ["exalted"] = "Exalted Orb",
        ["alch"] = "Orb of Alchemy", ["alchemy"] = "Orb of Alchemy", ["fusing"] = "Orb of Fusing", ["alt"] = "Orb of Alteration",
        ["aug"] = "Orb of Augmentation", ["regal"] = "Regal Orb", ["vaal"] = "Vaal Orb", ["annul"] = "Orb of Annulment",
        ["scour"] = "Orb of Scouring", ["chance"] = "Orb of Chance", ["transmute"] = "Orb of Transmutation",
        ["wisdom"] = "Scroll of Wisdom", ["whetstone"] = "Blacksmith's Whetstone", ["gcp"] = "Gemcutter's Prism",
        ["chisel"] = "Cartographer's Chisel", ["mirror"] = "Mirror of Kalandra", ["scrap"] = "Armourer's Scrap",
        ["bauble"] = "Glassblower's Bauble", ["blessing"] = "Blessing of the Cloud", ["regret"] = "Orb of Regret",
    };
}
