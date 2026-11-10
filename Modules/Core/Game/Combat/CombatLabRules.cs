using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

static partial class Game
{
    // The in-game combat lab's rules (Agents/docs/dev/combat-lab.md). The chat commands own no damage math: they reach
    // the ordinary skill path through OnClientMessages, and read back what the hit actually did from DamageLedger.
    // Nothing here invents a hit; a receipt only says the request was handed over.

    internal sealed record SkillMatch(ushort Id, int Effect, string Name, int Level, int Quality,
        IReadOnlyList<string> Candidates);

    /// <summary>
    /// One of this character's own castable gems by id (decimal or 0x hex), exact name, or unique name substring.
    /// "attack"/"default" is the default attack (SkillCatalog.FirstSkillId). Finds several: Candidates names them and
    /// Id is 0, so a caller can refuse and list them; finds none: Id is 0 and Candidates is empty.
    /// </summary>
    internal static SkillMatch SkillMatchByName(ClientState conn, string name)
    {
        name = name.Trim();
        var gems = GemSkillEffects(conn).ToList();
        if (name.Equals("attack", StringComparison.OrdinalIgnoreCase) || name.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            var attack = gems.FirstOrDefault(g => g.Id == SkillCatalog.FirstSkillId);
            return new(SkillCatalog.FirstSkillId, attack.Effect, attack.Name ?? "default attack", attack.Level, attack.Quality, []);
        }
        if (ushort.TryParse(name, out ushort id) && gems.Any(g => g.Id == id))
            return Match(gems.First(g => g.Id == id), []);
        if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ushort.TryParse(name[2..], System.Globalization.NumberStyles.HexNumber, null, out ushort hex)
            && gems.Any(g => g.Id == hex))
            return Match(gems.First(g => g.Id == hex), []);
        var exact = gems.Where(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return Match(exact[0], []);
        if (exact.Count > 1) return new(0, -1, "", 0, 0, [.. exact.Select(g => Describe(g))]);
        var partial = gems.Where(g => g.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? Match(partial[0], []) : new(0, -1, "", 0, 0, [.. partial.Select(g => Describe(g))]);
    }

    static SkillMatch Match((ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, IReadOnlyList<string> candidates) =>
        new(gem.Id, gem.Effect, gem.Name, gem.Level, gem.Quality, candidates);

    internal static string Describe((ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem) =>
        $"0x{gem.Id:x4} {gem.Name}";

    /// <summary>Every castable gem of this connection, one line each: id, name, level, quality, whether it needs a
    /// target and whether the ordinary pipeline would accept a use right now.</summary>
    internal static string SkillsListing(ClientState conn)
    {
        var lines = new List<string>();
        foreach (var gem in GemSkillEffects(conn).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!SkillsFor(conn).IsGemSkill(gem.Id)) continue;
            string kind = SkillsFor(conn).IsAttack(gem.Effect) ? "attack" : SkillsFor(conn).IsSpell(gem.Effect) ? "spell" : "other";
            string target = SkillNeedsTarget(conn, gem.Effect) ? "target=yes" : "target=no";
            lines.Add($"0x{gem.Id:x4} {gem.Name} lvl {gem.Level} q{gem.Quality} {kind} {target} {SkillReadiness(conn, gem)}");
        }
        if (lines.Count == 0) return "No castable gems in this character's slots.";
        lines.Add($"{lines.Count} gem(s).");
        return string.Join('\n', lines);
    }

    /// <summary>Whether a gem's skill wants an object: a projectile hits the cell it is aimed at, an attack swings at
    /// one, and a cast spell with a cast time is aimed; an instant self/spell (Righteous Fire) needs no target.</summary>
    static bool SkillNeedsTarget(ClientState conn, int effect) =>
        SkillsFor(conn).IsProjectile(effect) || SkillsFor(conn).IsAttack(effect)
        || SkillsFor(conn).IsSpell(effect) && CastMs(conn, effect, 0) is > 0;

    /// <summary>"ready", or the reason the ordinary skill path would refuse the use.</summary>
    internal static string SkillReadiness(ClientState conn, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return "not ready (leaving, closed or dead)";
        if (conn.Paused) return "not ready (paused)";
        if (GemSkillInstanceDisabled(conn, gem.Effect, gem.Id) is { } disabled) return $"not ready ({disabled})";
        double left = Math.Max(0, conn.SkillReadyAt.GetValueOrDefault(gem.Effect) - now);
        if (left > 0) return $"cooldown {left:0.0}s";
        uint cost = (uint)Math.Max(0, SupportedStats(conn, gem.Id).GetValueOrDefault(Stats.Key("mana_cost") ?? 0));
        if (cost > 0 && UnreservedMana(conn) < cost) return $"no mana ({UnreservedMana(conn)}/{cost})";
        return "ready";
    }

    /// <summary>A living hostile monster of this connection by id (decimal or 0x hex) or unique label substring (case
    /// ignored). Finds several: Matches names them and Id is 0; finds none: Id is 0 and Matches is empty.</summary>
    internal static (uint Id, string Label, IReadOnlyList<string> Matches) MonsterMatchByName(ClientState conn, string name)
    {
        name = name.Trim();
        lock (conn.Monsters)
        {
            var living = conn.Monsters.Where(e => e.Value is { Hostile: true, Dead: false, Life: > 0 }).ToList();
            if (uint.TryParse(name, out uint id) || name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(name[2..], System.Globalization.NumberStyles.HexNumber, null, out id))
            {
                if (living.FirstOrDefault(e => e.Key == id) is { Value: not null } hit)
                    return (id, hit.Value.Label, []);
                return (0, "", []);
            }
            var found = living.Where(e => e.Value.Label.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (found.Count == 1) return (found[0].Key, found[0].Value.Label, []);
            return (0, "", [.. found.Select(e => $"0x{e.Key:x} {e.Value.Label}")]);
        }
    }

    /// <summary>
    /// The player's totems as the lab sees them, one line each, in the style of <see cref="SkillsListing"/>: id,
    /// variety, level, life/maximum life, cell, seconds left and the skill it carries. They live in
    /// <c>ClientState.Totems</c> and never in <c>ClientState.Monsters</c> (the whole point of the separate collection:
    /// area hits, monster AI, loot, experience and the instance save must not see them), so <c>/mobs</c> and the
    /// <c>/state</c> <c>monsters</c> array cannot list them and this is the only way to answer "was the totem summoned
    /// at all?". Read-only; the Monsters lock guards the collection and the connection clock.
    /// </summary>
    internal static string TotemsListing(ClientState conn)
    {
        var lines = new List<string>();
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            foreach (var totem in conn.Totems.Living(now).OrderBy(t => t.Id))
            {
                string variety = totem.VarietyId.Length == 0 ? "-" : totem.VarietyId;
                double left = Math.Max(0, totem.Until - now);
                lines.Add($"0x{totem.Id:x} {variety}#{totem.Row} lvl {totem.Level} life {totem.Life}/{totem.MaxLife} " +
                    $"at ({totem.At.X},{totem.At.Y}) left {left:0.0}s skill {totem.SkillId} " +
                    $"({TotemSkillLabel(conn, totem.Effect)}) {TotemEffect(totem)}");
            }
        }
        if (lines.Count == 0) return "No totems.";
        lines.Add($"{lines.Count} totem(s).");
        return string.Join('\n', lines);
    }

    /// <summary>What a totem does with its skill, for the /totems line: whether the use is an attack or a spell, the
    /// cells it covers and how often it uses it, else the aura it carries, else that it only places.</summary>
    static string TotemEffect(TotemState.Totem totem) => totem.Strike is { } strike
        ? $"{(strike.Spell ? "spell" : "attack")} r{strike.Radius} every {strike.Interval:0.##}s"
        : totem.Devour is { } devour ? $"devours {devour.Life} life/{devour.Mana} mana a corpse"
        : totem.Aura is { } aura ? $"aura {aura.Row} r{aura.Radius}"
        : "no effect of its own";

    /// <summary>The last records as the lab file's lines: one compact JSON object per line, oldest first.</summary>
    internal static string DamageLog(ClientState conn, int count, string? filter) =>
        string.Join('\n', DamageLedger.Recent(conn, count)
            .Where(r => filter is null || r.Skill.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Target.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(DamageLedger.Json));

    /// <summary>The lab file of this character: logs/damage-&lt;character&gt;.jsonl, relative to the process working
    /// directory (the repository root).</summary>
    internal static string DamageLogPath(ClientState conn) =>
        Path.Combine("logs", $"damage-{SafeFileName(CharacterName(conn))}.jsonl");

    /// <summary>The last records as JSONL, rewritten so the file holds exactly what the ledger held this moment.</summary>
    internal static string WriteDamageLog(ClientState conn, int count, string? filter)
    {
        string path = DamageLogPath(conn);
        var records = DamageLedger.Recent(conn, Math.Clamp(count, 1, DamageLedger.Capacity))
            .Where(r => filter is null || r.Skill.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Target.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
        File.WriteAllText(path, records.Count == 0 ? "" : string.Join('\n', records.Select(DamageLedger.Json)) + "\n");
        return path;
    }

    static string SafeFileName(string name)
    {
        var text = new System.Text.StringBuilder(name.Length);
        foreach (char c in name) text.Append(Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c);
        string safe = text.ToString();
        return safe.Length == 0 ? "Exile" : safe;
    }

    /// <summary>Uses a gem through the ordinary client-message path, exactly as the client's own 0x24 would.</summary>
    internal static async Task UseSkillThroughPipeline(ClientState conn, ushort gemId, uint target, int x, int y)
    {
        string remote = conn.Wire.Remote;
        var stream = conn.Wire.Stream;
        var ciphers = conn.Wire.Ciphers;
        if (target != 0)
        {
            byte[] bytes = new LoginWriter().U16(InstanceTransfer.UseSkillOnObjectId).U32(target)
                .U16(gemId).U16(0xffff).U16(1).ToArray();
            await OnClientMessages(remote, stream, ciphers, [(InstanceTransfer.UseSkillOnObjectId, bytes)], [], conn);
            return;
        }
        byte[] at = new LoginWriter().U16(InstanceProtocol.UseSkillAtLocationId).U32((uint)x).U32((uint)y)
            .U16(gemId).U16(0xffff).U16(1).ToArray();
        await OnClientMessages(remote, stream, ciphers, [(InstanceProtocol.UseSkillAtLocationId, at)], [], conn);
    }

    /// <summary>
    /// Puts one of the character's own inventory gems into a socket of an equipped item through the ordinary
    /// server-side socket operation (the one the client's C→S 0x30 performs: PlayerInventories.Socket), sending the
    /// same inventory-change packets, so the gem really is socketed and its skill appears in /skills — a lab test can
    /// choose which skill is under test instead of only using what the character happened to have socketed.
    /// <paramref name="gem"/> matches the gem in the main inventory by its id (decimal or 0x hex) or by its name, the
    /// way /skills names it; <paramref name="item"/> names the equipped item by its item id or by a substring of its
    /// name, null takes the first equipped item with a free socket; <paramref name="socket"/> is the socket index, null
    /// the first free one. Answers one short line naming the gem, the item and the socket, or a readable reason it
    /// could not.
    /// </summary>
    internal static async Task<string> SocketGemForLab(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, string gem, string? item, int? socket)
    {
        var items = ItemsOf(conn);
        if (conn.Wire.Stream is null || conn.Wire.Ciphers is null) return "the connection has no open wire";
        if (items.Items(PlayerInventories.Cursor) is { Count: > 0 })
            return "cannot socket a gem while something is on the cursor";
        var gemTypes = LabGemTypes(conn, gem);
        if (gemTypes.Count == 0) return $"no gem in your inventory matches \"{gem}\"";
        if (gemTypes.Count > 1)
            return $"\"{gem}\" matches {gemTypes.Count} gems in the inventory, say which one: {string.Join(", ", gemTypes.Select(t => t.Name))}";
        var (gemType, gemName) = gemTypes[0];
        var candidates = LabSocketTargets(conn, item);
        if (candidates.Count == 0)
            return item is null
                ? "no equipped item has a free socket"
                : $"no equipped item matches \"{item}\" with a free socket";
        if (candidates.Count > 1)
            return $"\"{item}\" matches {candidates.Count} equipped items, name one of them by its item id: {string.Join(", ", candidates.Select(c => $"0x{c.Worn.Item:x} {c.Name}"))}";
        var (row, worn, wornName, wornFree) = candidates[0];
        int index = socket ?? wornFree[0];
        if (socket is { } asked && !wornFree.Contains(asked))
            return index < 0 || index >= worn.Item.Sockets.Count
                ? $"item \"{wornName}\" has no socket {index} ({worn.Item.Sockets.Count} socket(s))"
                : $"socket {index} of \"{wornName}\" already holds a gem (free: {string.Join(", ", wornFree)})";
        // The character's own requirement check, before anything moves: a gem he cannot use is refused here rather than
        // socketed (and left on the cursor) by the ordinary path.
        if (GemRequirementsUnmet(conn, gemType.Item) is { } unmet) return $"{gemName} cannot be socketed: {unmet}";
        // What the client's C→S 0x30 does: the gem is picked up to the cursor, then socketed; the socket's own gem (if
        // any) ends up on the cursor. PlayerInventories.Socket sends no packets, so send them here as the client's
        // 0x30 handler does, then refresh the skill list so the new skill appears in /skills and can be cast.
        if (items.PickUp(PlayerInventories.MainInventory, gemType.Id) is not { } held)
            return $"{gemName} is not in the main inventory any more";
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
            items.NextRevision(PlayerInventories.MainInventory), [held.Id], []),
            $"0x00bf inventory 1: /socket_gem picks up {gemName} ({held.Id}) to the cursor");
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [], [held]), $"0x00bf cursor: /socket_gem holds {gemName} ({held.Id})");
        var done = items.Socket(row, worn.Id, index, ShapeOf, items.NewItemId, out string why);
        if (done is not var (after, before, now))
            return $"{gemName} could not be socketed into \"{wornName}\": {why}";
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [before.Id], now is null ? [] : [now]),
            $"0x00bf cursor: remove gem {before.Id}" + (now is null ? "" : $", hold gem {now.Id} from the socket"));
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row),
            items.NextRevision(row), [after.Id], [after]),
            $"0x00bf inventory {PlayerInventories.InventoryId(row)}: item {after.Id} with {gemName} in socket {index}");
        if (PlayerInventories.ShowsOnModel(row)) await SendEquipment(remote, stream, ciphers, conn, items);
        await SendSkills(remote, stream, ciphers, conn);
        return $"socketed {gemName} into \"{wornName}\" (item 0x{worn.Item:x}) socket {index}; /skills now lists it";
    }

    /// <summary>The gems of this connection's main inventory that <paramref name="query"/> names, by id (decimal or 0x
    /// hex) or by the name /skills shows, exact first then substring, each with the name it matched.</summary>
    internal static IReadOnlyList<(InventoryItem Item, string Name)> LabGemTypes(ClientState conn, string query)
    {
        query = query.Trim();
        var found = new List<(InventoryItem Item, string Name)>();
        foreach (var held in ItemsOf(conn).Items(PlayerInventories.MainInventory))
            if (ItemsFor(conn).ByHash(held.Item.TypeHash) is { } type && ItemsFor(conn).Gem(type) is not null)
                found.Add((held, ItemsFor(conn).GemName(held.Item)));
        if (ushort.TryParse(query, out ushort id)) return [.. found.Where(g => g.Item.Id == id)];
        if (query.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ushort.TryParse(query[2..], System.Globalization.NumberStyles.HexNumber, null, out ushort hex))
            return [.. found.Where(g => g.Item.Id == hex)];
        var exact = found.Where(g => string.Equals(g.Name, query, StringComparison.OrdinalIgnoreCase)).ToList();
        return exact.Count > 0 ? exact : [.. found.Where(g => g.Name.Contains(query, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>The equipped items with a free socket, in the equipment's own row order. <paramref name="query"/> null
    /// takes all of them; otherwise it names one by item id (decimal or 0x hex) or by a substring of its base name.
    /// <c>Worn</c> is the inventory entry, so <c>Worn.Item</c> is the item record and <c>Worn.Id</c> its object id.</summary>
    internal static IReadOnlyList<(byte Row, InventoryItem Worn, string Name, List<int> Free)> LabSocketTargets(ClientState conn, string? query)
    {
        query = query?.Trim();
        var items = ItemsOf(conn);
        var found = new List<(byte Row, InventoryItem Worn, string Name, List<int> Free)>();
        foreach (byte row in LabEquipmentRows)
            foreach (var held in items.Items(row))
            {
                if (held.Item.Sockets.Count == 0) continue;
                if (query is not null && !LabItemMatches(conn, held, query)) continue;
                var free = Enumerable.Range(0, held.Item.Sockets.Count).Where(s => held.Item.Sockets[s].Gem is null).ToList();
                if (free.Count == 0) continue;
                string name = ItemsFor(conn).ByHash(held.Item.TypeHash) is { } type ? type.Name : $"item 0x{held.Id:x}";
                found.Add((row, held, name, free));
            }
        return found;
    }

    static bool LabItemMatches(ClientState conn, InventoryItem held, string query)
    {
        if (uint.TryParse(query, out uint id)) return held.Id == id;
        if (query.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(query[2..], System.Globalization.NumberStyles.HexNumber, null, out uint hex)) return held.Id == hex;
        return ItemsFor(conn).ByHash(held.Item.TypeHash) is { } type
            && type.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    static readonly byte[] LabEquipmentRows = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 14, 15];

    /// <summary>
    /// Records a skill use the input path dropped before the ordinary handler ran, reading the skill id and the aim
    /// from the message itself. Without it a use can be accepted, print its receipt and leave no trace at all — the
    /// worst case for a test harness, which sees "accepted" and "0 records" (Kind "refused", reason in outcome).
    /// </summary>
    internal static void NoteDroppedMessage(ClientState conn, ushort id, byte[] bytes, string reason)
    {
        if (id == InstanceTransfer.UseSkillOnObjectId)
        {
            var use = InstanceTransfer.ReadUseSkillOnObject(bytes);
            NoteRefusal(conn, use.SkillId, -1, null, reason, use.TargetId);
        }
        else if (id == InstanceProtocol.UseSkillAtLocationId)
        {
            var use = InstanceProtocol.ReadUseSkillAtLocation(bytes);
            NoteRefusal(conn, use.SkillId, -1, null, reason, 0, (use.X, use.Y));
        }
    }

    /// <summary>The largest deliberate hit /smite takes: above every monster's life, below any overflow, and still a
    /// value a caller can mistype into being refused rather than accepted.</summary>
    internal const int MaxSmiteDamage = 1_000_000_000;

    /// <summary>The damage types /smite accepts: the names the ledger prints — physical, lightning, cold, fire and
    /// chaos. Empty or absent is physical.</summary>
    internal static bool TryDamageType(string? text, out DamageType type)
    {
        type = DamageType.Physical;
        return string.IsNullOrWhiteSpace(text) || Enum.TryParse(text, ignoreCase: true, out type) && Enum.IsDefined(type);
    }

    /// <summary>
    /// The life /mob_life asks for: an absolute value, or n% of the maximum. False with a readable
    /// <paramref name="error"/> when the text is not a life value, the percentage is outside 0..100, or the absolute
    /// value is above the maximum — no silent clamping, so a test tool always says what it refused.
    /// </summary>
    internal static bool TryMobLife(string text, uint maximum, out uint target, out string? error)
    {
        target = 0;
        error = null;
        bool percent = text.EndsWith('%');
        if (!long.TryParse(percent ? text[..^1] : text, out long value))
        {
            error = $"\"{text}\" is not a life value or a percentage like 50%";
            return false;
        }
        if (percent)
        {
            if (value is < 0 or > 100)
            {
                error = $"a percentage must be 0 to 100 (got {value}%)";
                return false;
            }
            target = (uint)((ulong)maximum * (ulong)value / 100);
            return true;
        }
        if (value < 0)
        {
            error = $"life cannot be negative (got {value})";
            return false;
        }
        if ((ulong)value > maximum)
        {
            error = $"{value} is above its maximum life {maximum}";
            return false;
        }
        target = (uint)value;
        return true;
    }

    /// <summary>
    /// One deliberate hit under the Monsters lock, through the ordinary <c>ApplyMonsterHitLocked</c> — so the life
    /// really drops, an encounter's phase gate and stage machine see it, the on-death path runs, and the ledger
    /// records it as Kind "outgoing" under the unmistakable name "dev-smite". <paramref name="damage"/> is applied
    /// as-is: no evasion, block or mitigation roll, because it is an injected amount and not a formula's result. The
    /// monster's own invulnerability, phase gate and stage rules still apply. Null when it took nothing at all.
    /// </summary>
    internal static (uint Before, uint After, bool StageStarts)? SmiteLocked(ClientState conn, uint id, SpawnedMonster m,
        int damage, DamageType type, double at)
    {
        lock (conn.Monsters)
        {
            if (m.Dead || m.Life == 0) return null;
            uint before = m.Life;
            if (ApplyMonsterHitLocked(conn, m, damage) is not { } hit) return null;
            NoteOutgoing(conn, "dev-smite", 0, id, m, DamagePacket.Of(type, damage, damage), damage, false, before, at);
            return (before, hit.Life, hit.StageStarts);
        }
    }

    /// <summary>/smite: one deliberate hit on a monster, resolved exactly as /cast resolves its target.</summary>
    internal static async Task<string> SmiteForLab(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, string monster, int damage, string? type)
    {
        if (damage is <= 0 or > MaxSmiteDamage) return $"damage must be 1 to {MaxSmiteDamage} (got {damage})";
        if (!TryDamageType(type, out var kind))
            return $"unknown damage type \"{type}\" - use physical, lightning, cold, fire or chaos";
        var found = MonsterMatchByName(conn, monster);
        if (found.Id == 0)
            return found.Matches.Count == 0 ? $"no living monster matches \"{monster}\""
                : $"\"{monster}\" matches {found.Matches.Count} monsters: {string.Join(", ", found.Matches)}";
        SpawnedMonster target;
        lock (conn.Monsters)
        {
            if (!conn.Monsters.TryGetValue(found.Id, out target!) || target.Dead || target.Life == 0)
                return $"monster 0x{found.Id:x} is already dead";
            if (!target.Hostile) return $"monster 0x{found.Id:x} ({target.Label}) is not hostile";
        }
        double at = conn.Clock.Elapsed.TotalSeconds;
        if (SmiteLocked(conn, found.Id, target, damage, kind, at) is not { } result)
            return $"monster 0x{found.Id:x} ({target.Label}) took no hit (it is dead, or a grace refuses damage)";
        // Say when the life line did not move: an invulnerable intermission is the usual reason, and an unchanged
        // number that says nothing would look exactly like a broken command.
        string unchanged = result.After == result.Before
            ? target.Invulnerable ? " (unchanged: it is invulnerable while its phase runs)" : " (unchanged: its life floor refused the hit)"
            : "";
        var hit = new MonsterHit(result.After, result.StageStarts);
        if (hit.StageStarts)
        {
            CommitBossStage(remote, stream, ciphers, conn, found.Id, target, at);
            hit = hit with { StageStarts = false };
        }
        await NotifyMonsterHit(remote, stream, ciphers, conn, found.Id, target, hit, damage, "dev-smite");
        return $"smite 0x{found.Id:x} {target.Label} for {damage} {kind.ToString().ToLowerInvariant()} - " +
            $"life {result.Before} -> {result.After}{(result.After == 0 ? ", killed" : "")}{unchanged}";
    }

    /// <summary>
    /// /mob_life: set a monster's current life to an absolute value or to n% of its maximum, for jumping straight to
    /// a phase threshold. The life change goes to the client as the ordinary 0x1b1 life update. Zero is a lethal
    /// demand and is NOT written: it runs as a dev-smite of the monster's whole life, so the ordinary death handling
    /// happens and a monster can never sit at 0 life while the server still thinks it is alive.
    /// </summary>
    internal static async Task<string> MobLifeForLab(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, string monster, string value)
    {
        var found = MonsterMatchByName(conn, monster);
        if (found.Id == 0)
            return found.Matches.Count == 0 ? $"no living monster matches \"{monster}\""
                : $"\"{monster}\" matches {found.Matches.Count} monsters: {string.Join(", ", found.Matches)}";
        SpawnedMonster target;
        uint before, maximum;
        lock (conn.Monsters)
        {
            if (!conn.Monsters.TryGetValue(found.Id, out target!) || target.Dead || target.Life == 0)
                return $"monster 0x{found.Id:x} is already dead";
            if (!target.Hostile) return $"monster 0x{found.Id:x} ({target.Label}) is not hostile";
            before = target.Life;
            maximum = target.MaxLife;
        }
        if (!TryMobLife(value, maximum, out uint wanted, out string? error)) return error!;
        if (wanted == 0)
            return await SmiteForLab(remote, stream, ciphers, conn, monster, (int)Math.Min(before, int.MaxValue), null)
                + " - mob_life 0 is a lethal dev-smite, so the ordinary death path runs";
        lock (conn.Monsters)
        {
            if (target.Dead || target.Life == 0) return $"monster 0x{found.Id:x} died before the change";
            target.Life = wanted;
        }
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(found.Id, wanted),
            $"0x01b1 monster 0x{found.Id:x} life set to {wanted}/{maximum} by /mob_life");
        double percent = maximum == 0 ? 0 : (double)wanted * 100 / maximum;
        return $"mob_life 0x{found.Id:x} {target.Label} - max {maximum}, life {before} -> {wanted} ({percent:0.#}%)";
    }

    /// <summary>Spawns a monster for the lab through the ordinary spawn path (/spawn_mob's own SpawnMonster) at
    /// dx/dy cells from where the player stands, and answers one compact JSON object: the id, the cell it landed on,
    /// the level, the rarity and the monster's name. An id of 0 means the ordinary spawn path refused (closed or
    /// leaving area). The caller reads the id from the JSON; there is no human sentence to parse.</summary>
    internal static async Task<string> SpawnLabMonster(ClientState conn, MonsterCatalog.Variety variety, int level,
        MonsterRarity rarity, string? name, int dx, int dy, bool friendly)
    {
        int at = Math.Clamp(level, 1, 100);
        var here = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        var (x, y) = conn.SpawnNear((here.X + dx, here.Y + dy));
        uint id = await SpawnMonster(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn,
            variety, (byte)at, rarity, x, y, name, friendly);
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            id, x, y, level = at, rarity = rarity.ToString().ToLowerInvariant(), monster = variety.Name,
        });
    }
}
