# The market: the official economy, copied

Path of Exile's economy has three faces, and the client in `Client/` has a UI for each: **the trade site indexes public
premium tabs** and a buyer then meets the seller; **Faustus's currency exchange** matches currency orders for a gold fee
with nobody waiting online; and 3.29's **async trade** (the "Puppet of Corruption" in a hideout) lets a seller put items
up in a shop window a buyer purchases from directly, the seller collecting later. This design copies them in that order,
so a listing, a price and a trade mean here what they mean there.

## Listings (the stash tab, not a separate warehouse)

- Only a premium tab can be public, and it can be switched private again at any time — which unlists everything in it at
  once. `AccountStash.IsPremium` already draws that line (every type but Normal 0 and Folder 16).
- A price is a **note**, in the client's own syntax: `~price 5 chaos` (fixed), `~b/o 2 divine` (buyout), `~offer`,
  `~free`. Case-insensitive; the currency keywords are the game's names (`chaos`, `divine`, `exalted`, `alch`, `fusing` …).
- **An item's own note beats the tab's note**, an item with no note takes the tab's, and an item with no note at all is
  not listed.
- **The item never leaves the tab.** The listing *is* the item sitting there: no escrow, no reserved stock, no fee, and
  taking the item out or making the tab private unlists it. Moving, selling or pricing an item in game is reflected at
  once, because both read the same `AccountStash`.
- Saved state: `SavedTab.Public`, `SavedTab.Note` and per-item notes, saved with the stash beside `Affinities`, so they
  survive a relog like the name and colour do.

## The index (our page, the trade site's role)

- Only public premium tabs are indexed; a tab that goes private drops out on the next read.
- A listing shows the item, its class, rarity, item level, quality, stack and mod count, the price, the seller, and
  whether the seller is **online / offline** (an offline seller is still listed, which is what the game does).
- Filters: name, class, rarity, currency, price range, online only.
- The official whisper text, ready to paste, one button per listing:
  `@<seller> Hi, I'd like to buy your <item> listed for <price> in <league>`
- Completion by whisper, party invite, hideout and the trade window is already implemented in the game module
  (`Modules/Core/Player/Social/SocialModule.cs`, `PlayerTrade`), so that half is the game's own code.

## Faustus: currency matched for a gold fee

The currency exchange is not a shop and not a meeting: it is an **order book**. A player says what they give, what they
want and how much; the exchange matches it against other players' orders, keeps the difference when an order is only
partly filled, and takes a **fee in gold** (the account's gold, `AccountStash.Gold`) that grows with the size of the
exchange. Nobody has to be online at the same time: the filled part waits to be collected, which is why the game shows an
icon over the NPC when there is something to pick up. Solo Self-Found refuses the exchange, as it refuses the guild stash.

The client's own window is `CurrencyExchangeWindow` (S→C 0x7b kind 0x5e), the NPC is the "Puppet of Corruption" (NPCs row
819), and `CanSeeFaustusInHideout` is the flag the client checks — `17-npcs.md` §"NPC options" and `21-hideouts.md`
document both, and `NpcDialogueModule` already opens the window; what is missing is the window's data.

## Async trade: the item half of the same idea

3.29 gives the same NPC two more rows: **Purchase Items** (`BuyAsyncTrade`, the ordinary 0x8c buy window with the
listings in it) and **Manage Shop** (`AsyncTradeStashWindow`, S→C 0x7b kind 0x63), where the seller's stock lives. A buyer
purchases without the seller being there, and the seller's earnings wait to be collected. Both windows are documented as
**not implemented** in this server. They are the faithful target for item selling: it is the one design in which a listing
can be bought while its owner is asleep, and it is the game's own UI rather than a page of ours.

## What is built, and what is next

Built: the listing model and its note grammar (`Market/MarketNote.cs`), the index and its filters
(`Market/MarketIndex.cs`), the write that lists a tab and prices items (`Market/MarketAdmin.cs`, a signed-in player for
their own account or the owner's token for any), the sign-in and its tokens (`Market/MarketSession.cs`, the game's own
account and password), **buying with nobody in game** (`Market/MarketTrade.cs`), and **Faustus's currency book on the page**
(`Market/MarketExchange.cs`): an order's offer is escrowed the moment it is placed, the book matches the opposite order at
an equal or better rate, fills it whole or in part, pays both sides into their market tabs and takes the gold fee from each
for what it receives; cancelling returns what is left, and the orders are saved with the account's stash. The page has the
market panel (filters, listings, seller state, the whisper button), the sign-in row, a buy button per listing, and the
exchange panel (give, want, place, cancel and the open book). All of it against the client's own rules, and all of it
checked on a copy of the save database.

Next:

1. **The client's windows** — `CurrencyExchangeWindow` and `AsyncTradeStashWindow` with the async listings, so the page's
   two flows happen in the game's own UI instead: the schema classes are already located (case 0x5e and case 0x63), the
   dialogue rows already open the windows, and only the data is missing.
2. **Collecting, as the game does it** — Faustus keeps what was filled until its owner comes for it, with an icon over the
   NPC; ours delivers straight into the market tab, which is the one place we are more convenient than the game.
3. **Following the client's note message** — the public check box and the note box of the tab settings window (0xd2 and
   the message beside it are still "not seen"), so a listing is priced in game rather than on the page.
