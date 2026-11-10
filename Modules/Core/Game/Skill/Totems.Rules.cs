using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core;
using DamageModel = GameServer.Core.World.DamageModel;

static partial class Game
{
    /// <summary>"X% of Damage from Hits is taken from your nearest Totem's Life before you": Forest of Ancestors' 20 and
    /// Kaom's Leadership's 25 add.</summary>
    internal const string TotemRedirectStat = "damage_removed_from_your_nearest_totem_before_life_or_es_%";

    /// <summary>Metadata/Characters/Character.ot: base_number_of_totems_allowed 1 and base_presence_radius 80 (the wiki's
    /// fixed 8 metres "in your presence"). The client has both from the .ot; the server's stats do not carry them.</summary>
    internal const int BaseTotemsAllowed = 1, PresenceRadius = 80;

    /// <summary>The totem skills that also carry an aura: Rejuvenation Totem's (BuffDefinitions 172
    /// totem_aura_life_regen, base radius 10, wiki Rejuvenation Totem) regenerates the player's Life. A totem skill that
    /// is not here still places its totem and uses its own skill (TotemUseLocked); only the aura is special. An
    /// unsupported totem payload is refused at preflight.</summary>
    static readonly Dictionary<string, (ushort Buff, int Radius)> TotemAuras = new() { ["RejuvenationTotem"] = (172, 10) };

    /// <summary>The ActiveSkillType a totem skill's own ActiveSkills row carries. SkillTotemId cannot serve as the
    /// marker: every one of the 1941 rows has it, and 1906 share the default 22, so it names the sprite a
    /// Totem-Support conversion would use rather than "this skill summons a totem". SummonsTotem is what only the
    /// totem skills themselves carry (shockwave_totem, holy_flame_totem, ancestor_totem_slash, ...).</summary>
    internal const string TotemSkillType = "SummonsTotem";

    /// <summary>The granted-skill id a totem's object carries for the action the client plays, the id a placed trap or
    /// mine uses for its proxy skill.</summary>
    internal const ushort TotemActionSkill = 0x4000;

    /// <summary>The cells a totem looks for a target in (our number: the server's skill data carries no totem range).
    /// A target is hit with the skill's own radius around the point the use was aimed at.</summary>
    internal const int TotemRange = 60;

    /// <summary>The client's Emerge basic action (`Actor.basic_action` of `Metadata/Monsters/Totems/Totem`): the rise a
    /// totem plays when it appears. Every totem `.act` maps `Emerge`, and no totem variety has a Transitionable
    /// component, so the monster rise's 0x2bc does not apply — 0x1ab then 0x1ac with this id plays it at once.</summary>
    internal const ushort TotemEmergeSkill = 0x0101;

    // GrantedEffects row → MonsterVarieties row of its default skin: ActiveSkills.SkillTotemId is SkillTotemVariations'
    // SkillTotemsKey, TotemSkinId 0 (Rejuvenation Totem: 4 → Metadata/Monsters/Totems/LifeRegenTotem).
    static readonly Lazy<Dictionary<int, int>> TotemVarieties = new(() => LoadTotemVarieties("data/tables"));

    // The totem auras' BuffDefinitions stats (172: base_life_regeneration_rate_per_minute and five more); the totem's
    // skill gives their values.
    static readonly Lazy<Dictionary<ushort, uint[]>> TotemBuffStats = new(() =>
    {
        var result = new Dictionary<ushort, uint[]>();
        string path = System.IO.Path.Combine("data/tables", "BuffDefinitions.json");
        if (!ClientTables.Exists(path)) return result;
        using var definitions = JsonDocument.Parse(ClientTables.ReadAllBytes(path));
        foreach (var (row, _) in TotemAuras.Values)
            if (row < definitions.RootElement.GetArrayLength()
                && definitions.RootElement[row].TryGetProperty("StatsKeys", out var keys) && keys.ValueKind == JsonValueKind.Array)
                result[row] = [.. keys.EnumerateArray().Select(k => (uint)(k.GetInt32() + 1))];
        return result;
    });

    static Dictionary<int, int> LoadTotemVarieties(string tables)
    {
        string Path(string name) => System.IO.Path.Combine(tables, name + ".json");
        var result = new Dictionary<int, int>();
        if (!ClientTables.Exists(Path("GrantedEffects")) || !ClientTables.Exists(Path("ActiveSkills")) || !ClientTables.Exists(Path("SkillTotemVariations"))) return result;
        using var effects = JsonDocument.Parse(ClientTables.ReadAllBytes(Path("GrantedEffects")));
        using var active = JsonDocument.Parse(ClientTables.ReadAllBytes(Path("ActiveSkills")));
        using var variations = JsonDocument.Parse(ClientTables.ReadAllBytes(Path("SkillTotemVariations")));
        var skins = new Dictionary<int, int>();
        foreach (var v in variations.RootElement.EnumerateArray())
            if (v.GetProperty("TotemSkinId").GetInt32() == 0 && v.GetProperty("MonsterVarietiesKey") is { ValueKind: JsonValueKind.Number } row)
                skins.TryAdd(v.GetProperty("SkillTotemsKey").GetInt32(), row.GetInt32());
        var skills = active.RootElement.EnumerateArray().ToArray();
        int i = 0;
        foreach (var e in effects.RootElement.EnumerateArray())
        {
            if (e.TryGetProperty("ActiveSkill", out var a) && a.ValueKind == JsonValueKind.Number && a.GetInt32() < skills.Length
                && skills[a.GetInt32()].TryGetProperty("SkillTotemId", out var totem) && totem.ValueKind == JsonValueKind.Number
                && skins.TryGetValue(totem.GetInt32(), out int variety))
                result[i] = variety;
            i++;
        }
        return result;
    }

    /// <summary>Whether a gem skill summons a totem. The gem's own <c>is_totem</c> stat says so; a skill whose
    /// ActiveSkills row carries the SummonsTotem type says so too, which is the older way the client marked it.</summary>
    internal static bool IsTotemSkill(ClientState conn, ushort skill) =>
        SupportedSkill(conn, skill).Stats.GetValueOrDefault(Stats.Key("is_totem") ?? 0) != 0
        || SkillsFor(conn).HasType(SupportedSkill(conn, skill).Effect, TotemSkillType);

    /// <summary>The totem variety a totem skill summons, or null. The variety is the skill's own SkillTotemVariations
    /// entry (skin 0); a spell or ballista totem without one uses its generic skin (intelligence 3, dexterity 2).</summary>
    internal static MonsterCatalog.Variety? TotemVariety(ClientState conn, int effect)
    {
        if (SkillsFor(conn).EffectId(effect) is { } id && TotemAuras.ContainsKey(id)
            && TotemVarieties.Value.TryGetValue(effect, out int native))
            return Monsters.Find(native.ToString()) is { Supported: true } aura ? aura : null;
        // A skill whose ActiveSkills row carries the SummonsTotem type names its own sprite (skin 0).
        if (SkillsFor(conn).HasType(effect, TotemSkillType)
            && TotemVarieties.Value.TryGetValue(effect, out int row)
            && Monsters.Find(row.ToString()) is { Supported: true } variety) return variety;
        if (!SpellTotemEffect(conn, effect) && !BallistaEffect(conn, effect)) return null;
        // Prefer a dedicated native skin; generic spell/ballista proxies use intelligence/dexterity skins 3/2.
        if (TotemVarieties.Value.TryGetValue(effect, out int specific)
            && Monsters.Find(specific.ToString()) is { Supported: true } nativeSpell) return nativeSpell;
        using var variations = GameTables.Open("data/tables/SkillTotemVariations.json");
        var skin = variations.RootElement.EnumerateArray().FirstOrDefault(v => v.GetProperty("SkillTotemsKey").GetInt32() == (BallistaEffect(conn, effect) ? 2 : 3)
            && v.GetProperty("TotemSkinId").GetInt32() == 0);
        return skin.ValueKind == JsonValueKind.Object
            && Monsters.Find(skin.GetProperty("MonsterVarietiesKey").GetInt32().ToString()) is { Supported: true } spell ? spell : null;
    }

    /// <summary>Whether the totem of a skill may still be placed: no totem of it stands yet, the variety is known, the
    /// limit is above zero and the totem's own payload is one this server runs.</summary>
    internal static bool TotemPathAvailable(ClientState conn, ushort skill, int effect) => !IsTotemSkill(conn, skill)
        || PlacedKind(SupportedSkill(conn, skill)) is (false, false)
            && TotemVariety(conn, effect) is not null && TotemLimit(conn, skill) > 0
            && (TotemAuras.ContainsKey(SkillsFor(conn).EffectId(effect) ?? "")
                || SpellTotemEffect(conn, effect) && TotemCastSeconds(conn, skill) is not null
                || BallistaPathAvailable(conn, skill));

    // A totem skill's use, after its mana: the totem rises at the aimed point once the summon ends (GrantedEffects
    // CastTime, 0.6 s, over totem placement speed and action speed; wiki Totem: cast speed does not apply). That wait
    // does not end at the player's next action: the client plays the summon itself and lets him act long before this
    // wait is over (live 2026-11-09: a walk 0.38 s after the cast of a 1.0 s skill), so gating on the action token
    // dropped six of seven casts while their mana stayed paid. Death, an area change and a closed connection still
    // cancel it, and CreateTotem checks them again under the lock.
    // After placement pays its cost, the totem rises at the aimed point. Placement speed and owner action speed
    // scale this delay; the proxy's spell/weapon speed only controls its subsequent attacks.

    static async Task PlaceTotem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, int effect, int level, MonsterCatalog.Variety variety, (int X, int Y) at, Func<bool> current)
    {
        double factor, speed;
        lock (conn.Monsters) factor = conn.ElementalAilments.ActionFactor(conn.Clock.Elapsed.TotalSeconds);
        speed = Stats.Key("summon_totem_cast_speed_+%") is uint k ? SupportedStats(conn, skillId).GetValueOrDefault(k) : 0;
        // 3.28 changed ranged totem placement from 0.35 to 0.5 seconds (PoEDB version notes).
        double basis = BallistaEffect(conn, effect) ? .5 : SpellTotemEffect(conn, effect) ? .6
            : SkillsFor(conn).CastTimeMs(effect) / 1000.0;
        double seconds = basis / Math.Max(.01, 1 + speed / 100) / Math.Max(.01, factor);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        await WhilePaused(conn);
        if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || !current()) return;
        await CreateTotem(remote, stream, ciphers, conn, skillId, effect, level, variety, at, current);

    }

    /// <summary>
    /// Summons one totem of a skill at a point: Life from the variety's base at the gem's level requirement (PoB's totem
    /// level; the MinionLife column, as the client derives it) with totem_life_+%, duration base_totem_duration with
    /// totem_duration_+% only (wiki Totem), limit Character.ot's 1 plus number_of_additional_totems_allowed (the player's
    /// and the skill's), replacing the oldest. Its aura's stats and radius are the skill's at this moment. Null when the
    /// limit is below one (the client refuses the use itself then) or the player is gone.
    /// </summary>
    internal static async Task<uint?> CreateTotem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, int effect, int gemLevel, MonsterCatalog.Variety variety, (int X, int Y) at, Func<bool>? current = null)
    {
        if (!CurrentGameWork || conn.Closed || conn.LeavingArea) return null;
        var stats = SupportedStats(conn, skillId);
        var own = SupportedSkill(conn, skillId).Stats;
        int Get(string name) => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
        int limit = TotemLimit(conn, skillId);
        if (limit < 1) return null;
        byte level = (byte)Math.Clamp(Get("totem_support_gem_level") is > 0 and var supportLevel
            ? supportLevel : SkillsFor(conn).PlayerLevelReq(effect, gemLevel), 1, 100);
        int baseLife = Monsters.BaseLife(variety, level) ?? 1;
        uint life = (uint)Math.Clamp((long)baseLife * (100 + Get("totem_life_+%")) / 100, 1, int.MaxValue);
        double seconds = Get("base_totem_duration") / 1000.0 * Math.Max(0, 100 + Get("totem_duration_+%")) / 100;
        TotemState.Aura? aura = null;
        if (SkillsFor(conn).EffectId(effect) is { } id && TotemAuras.TryGetValue(id, out var source))
        {
            var area = MergeSkillStats(AreaPlayerStats(conn), own);
            int Area(string name) => (name == "skill_area_of_effect_+%" ? conn.Pools.AuraAreaPct : 0)
                + (Stats.Key(name) is uint k ? area.GetValueOrDefault(k) : 0);
            var buffStats = TotemBuffStats.Value.GetValueOrDefault(source.Buff) ?? [];
            aura = new(source.Buff, SkillArea.Radius(source.Radius, Area, spell: true),
                [.. buffStats.Where(own.ContainsKey).Select(s => (s, own[s])).Where(s => s.Item2 != 0)]);
        }
        // The totem's own skill: its object carries a granted proxy skill whose id the action names, as a placed trap's
        // proxy does, so the client can play what the totem does. Every totem skill gets one, damaging or not: a skill
        // with no hit (Rejuvenation, Decoy, Devouring Totem) still plays its action.
        bool spell = !SkillsFor(conn).HasType(effect, "Attack");
        int radius = SkillRadius(conn, own, spell, effect);
        var payload = own.Select(p => (p.Key, p.Value)).ToList();
        if (Stats.Key("active_skill_area_of_effect_radius") is uint radiusKey) payload.Add((radiusKey, radius));
        // A totem does not path: without this the client walks the actor into range and plays nothing (11-skills.md §3).
        payload.Add((StatKeys.SkillIsNonPathing, 1));
        GrantedSkill? action = SkillsFor(conn).FindByRow(TotemActionSkill, effect, gemLevel, payload) is { } granted
            ? granted with { Parent = (PlayerId, skillId) } : null;
        ushort grantedId = action is null ? (ushort)0 : TotemActionSkill;
        TotemState.Strike? strike = null;
        if (action is not null && SupportedHitFor(conn, skillId, suppress: false) is { IsZero: false } hit)
        {
            double interval = Math.Max(.3, SkillsFor(conn).CastTimeMs(effect) / 1000.0);
            strike = new(TotemActionSkill, hit, radius, TotemRange, spell, interval, Math.Max(.05, interval / 2))
            { Sources = new Dictionary<uint, int>(own) };
        }
        // The two totems whose effect is not a hit of their own: Devouring Totem eats the corpses near it (TotemDevourLocked)
        // and Decoy Totem draws the monsters to itself (TotemTauntLocked, read by the AI).
        string? effectId = SkillsFor(conn).EffectId(effect);
        int Perk(string name) => Get(name) is var value && value != 0 ? value : GolemStat(own, name);
        TotemState.Devour? devour = effectId == "DevouringTotem"
            ? new(Perk("corpse_consumption_life_to_gain"), Perk("corpse_consumption_mana_to_gain")) : null;
        bool taunt = effectId == "DecoyTotem";
        int bodySize = Math.Clamp(variety.ObjectSize - 1, -1, 127);
        (int X, int Y) place = (Math.Clamp(at.X, 0, Math.Max(0, conn.Layout.GridW - 1)), Math.Clamp(at.Y, 0, Math.Max(0, conn.Layout.GridH - 1)));
        var playerStats = CharacterPlayerStats(conn);
        bool ballista = BallistaEffect(conn, effect) && GolemStat(own, "is_ranged_attack_totem") != 0;
        bool spellTotem = (SpellTotemEffect(conn, effect) || ballista) && IsTotemSkill(conn, skillId);
        var sourceGem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId).Gem;
        var proxy = spellTotem ? TotemProxy(conn, skillId) : null;
        if (spellTotem && proxy is null) return null;
        uint? created = null;
        // Registered and created at the send gate, so the tick's presentation never sends a totem the client lacks.
        await Send(remote, stream, ciphers, [], null, atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || current?.Invoke() == false) return null;
                uint objectId = conn.NextObjectId++;
                if (bodySize >= 0)
                {
                    bool Floor(int cx, int cy) => conn.Layout.WalkableFor(cx, cy, bodySize) && !conn.Barrier.Contains((cx, cy));
                    if (CollisionEscape.Find(place, conn.Layout.GridW, conn.Layout.GridH, Floor, conn.MonsterPlacementBlockers(objectId),
                        moverSize: bodySize) is { } clear) place = clear;
                }
                double now = conn.Clock.Elapsed.TotalSeconds;
                var placed = new TotemState.Totem(objectId, skillId, place, life, now + seconds, aura, strike, grantedId, devour, taunt,
                    row: variety.Row, varietyId: variety.Id, level: level, effect: effect)
                { SourceGem = sourceGem, SourceHost = SupportedSkill(conn, skillId).Host,
                    SourceItem = SupportedSkill(conn, skillId).SourceGem };
                int ordinaryLimit = (int)Math.Clamp((long)BaseTotemsAllowed + GolemStat(playerStats, "number_of_additional_totems_allowed"), 0, int.MaxValue);
                placed.Ballista = ballista;
                placed.BonusSlots = ballista ? Math.Max(0, limit - ordinaryLimit) : 0;
                if (spellTotem) BindTotemSource(conn, placed);
                var replaced = conn.Totems.Place(placed, limit, now, ordinaryLimit);
                if (replaced.Count > 0) TotemsDiedLocked(conn, now, playerStats);
                // A totem blocks paths on the client as any body does.
                if (bodySize >= 0) conn.SetBlocker(objectId, place.X, place.Y, bodySize);
                var bytes = new List<byte>();
                if (conn.MonsterTypes.Add((variety.Row, level)))
                    bytes.AddRange(InstanceEntities.MonsterTypes([((ushort)variety.Row, level)]));
                var objectStats = new List<(uint, int)>();
                if (life != baseLife) objectStats.Add((StatKeys.MaximumLife, (int)life));
                if (spellTotem && Stats.Key("uses_no_mana") is uint noMana) objectStats.Add((noMana, 1));
                var blob = new MonsterObject(place.X, place.Y, life, variety.ComponentsOrBase, StateMachine: variety.StateMachine,
                    TransitionState: variety.TransitionStates, Team: 0,
                    Skills: spellTotem ? (proxy is null ? null : [proxy]) : action is null ? null : [action],
                    Stats: objectStats) { LifeData = variety.LifeData };
                bytes.AddRange(InstanceEntities.CreateObject(objectId,
                    InstanceEntities.ObjectTypeHash(MonsterObject.TypePath(variety.Id, level)), blob.Blob()));
                bytes.AddRange(InstanceProtocol.EnterWakeZone(objectId));
                // It rises where it appeared: the totem base's own basic action, at once (0x1ab then 0x1ac). A totem has
                // no Transitionable component, so the monster rise's 0x2bc is not sent and the actor needs no grant.
                bytes.AddRange(InstanceProtocol.ActionStartedNow(objectId, place.X, place.Y, place.X, place.Y,
                    TotemEmergeSkill, conn.NextActionSequence(), 0x0800, objectId));
                bytes.AddRange(TotemPresentation(remote, stream, ciphers, conn) ?? []);
                Console.WriteLine($"[gateway] {CharacterName(conn)}: totem 0x{objectId:x} {variety}@{level} at ({place.X}, {place.Y}), "
                    + $"life {life}, {seconds:0.##} s, limit {limit}, {replaced.Count} replaced"
                    + (aura is null ? "" : $", aura {aura.Row} radius {aura.Radius}")
                    + (grantedId == 0 ? ", no action skill" : $", action 0x{grantedId:x}")
                    + (strike is null ? "" : $" radius {strike.Radius} every {strike.Interval:0.##} s")
                    + (devour is null ? "" : $", devours {devour.Life} life / {devour.Mana} mana a corpse")
                    + (taunt ? ", taunts" : ""));
                created = objectId;
                return [.. bytes];
            }
        });
        return created;
    }

    /// <summary>
    /// Before Ward, Energy Shield and Life take a hit (DamagePlayerHit, under the Monsters lock): the redirect stat's share of
    /// each part goes to the nearest living totem's Life, capped by it (PoB ModParser "taken from your nearest totem's life
    /// before you", HaveTotem; reducePoolsByDamage). No distance limit applies. What the player still takes.
    /// </summary>
    internal static (int NonChaos, int Chaos) TotemsTakeHitLocked(ClientState conn, int nonChaos, int chaos, double now,
        IReadOnlyDictionary<uint, int> stats)
    {
        int pct = Stats.Key(TotemRedirectStat) is uint key ? stats.GetValueOrDefault(key) : 0;
        // The totems live on the connection clock; the hit's world time places the player.
        double clock = conn.Clock.Elapsed.TotalSeconds;
        if (pct <= 0 || conn.Totems.Nearest(conn.Walk.Position(now), clock) is not { } totem) return (nonChaos, chaos);
        var (keptNonChaos, life) = TotemState.Redirect(nonChaos, pct, totem.Life);
        var (keptChaos, left) = TotemState.Redirect(chaos, pct, life);
        totem.Life = left;
        if (left == 0)
        {
            totem.DiedAt = clock;
            TotemsDiedLocked(conn, clock, stats);
        }
        return (keptNonChaos, keptChaos);
    }

    // Totems died: with a living Unique enemy within the player's presence, Spirit of Kaom triggers (its node's
    // has_trigger_on_totem_death_in_presence_of_unique; the shared Spirit cooldown decides).
    static void TotemsDiedLocked(ClientState conn, double now, IReadOnlyDictionary<uint, int> stats)
    {
        if (conn.PlayerLife == 0) return;
        var here = conn.Walk.Position(now);
        if (conn.Monsters.Values.Any(m => m.Hostile && !m.Dead && m.Life > 0 && m.Rarity == MonsterRarity.Unique
            && Dist(m.Walk.Position(now), here) <= PresenceRadius))
            TriggerSpiritChieftainLocked(conn, TotemDeathInPresenceOfUniqueTrigger, stats);
    }

    // The aura of the totem covering the living player joins his stats (CharacterPlayerStatsCore), with his aura effect.
    static void AddTotemAuraStats(ClientState conn, Dictionary<uint, int> stats)
    {
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (conn.PlayerLife == 0 || conn.Totems.AuraAt(conn.Walk.Position(now), now)?.Aura is not { } aura) return;
            foreach (var (key, value) in aura.Stats)
                stats[key] = (int)Math.Clamp(stats.GetValueOrDefault(key)
                    + Math.Round(value * Math.Max(0, 100 + conn.Pools.AuraEffectPct) / 100.0, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue);
        }
    }

    // A totem with a skill of its own uses it: it aims at the nearest living hostile monster in its range (0x1ab from
    // the totem, naming the granted skill its object carries, as a monster's swing names its own) and the hit lands at
    // the contact. A totem that only carries an aura (Rejuvenation Totem) has no strike and does nothing here.
    static void TotemUseLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        TotemState.Totem totem, double now, List<byte> bytes)
    {
        if (conn.PlayerLife == 0) return;
        if (totem.Devour is { } devour) { TotemDevourLocked(remote, stream, ciphers, conn, totem, devour, now, bytes); return; }
        if (totem.Strike is not { } strike) return;
        if (strike.ContactAt is double contact && now >= contact)
        {
            TotemStrikeLocked(remote, stream, ciphers, conn, totem, strike, now);
            strike.ContactAt = null;
        }
        if (now < strike.NextUse || strike.ContactAt is not null) return;
        uint target = 0; (int X, int Y) aim = totem.At; double nearest = double.MaxValue;
        foreach (var (id, m) in conn.Monsters)
        {
            if (m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false }) continue;
            var at = m.Walk.Position(now);
            double distance = Dist(at, totem.At);
            if (distance > strike.Range || distance >= nearest) continue;
            nearest = distance; target = id; aim = at;
        }
        if (target == 0) return;
        bytes.AddRange(InstanceProtocol.ActionStarted(totem.Id, totem.At.X, totem.At.Y, aim.X, aim.Y, strike.GrantedId,
            0xffff, 0x0408, targetObjectId: target));
        strike.Target = target; strike.Aim = aim;
        strike.ContactAt = now + strike.Contact;
        strike.NextUse = now + strike.Interval;
    }

    // Devouring Totem: it eats the nearest corpse within its range (the engine's: a monster killed here whose Life type
    // allows corpse use and that nothing has consumed), giving the player the gem's own life and mana for it — the
    // Deathrattle warcry's corpse consumption (Warcries.cs) with Devour Support's clamp. The corpse the client holds goes
    // with 0x2bd, and the totem plays its action at it.
    static void TotemDevourLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        TotemState.Totem totem, TotemState.Devour devour, double now, List<byte> bytes)
    {
        if (now < devour.NextUse) return;
        uint corpseId = 0; SpawnedMonster? corpse = null; (int X, int Y) at = totem.At; double nearest = double.MaxValue;
        foreach (var (id, m) in conn.Monsters)
        {
            if (!IsCorpse(m)) continue;
            var cell = m.Walk.Position(now);
            double distance = Dist(cell, totem.At);
            if (distance > TotemRange || distance >= nearest) continue;
            nearest = distance; corpseId = id; corpse = m; at = cell;
        }
        if (corpse is null) return;
        corpse.CorpseConsumed = true;
        conn.RecentEvents.Record(StatConditions.ConsumedCorpse, now);
        conn.Monsters.Remove(corpseId);
        if (devour.Life > 0) conn.PlayerLife = (uint)Math.Min(conn.PlayerMaxLife, (long)conn.PlayerLife + devour.Life);
        if (devour.Mana > 0) conn.PlayerMana = (uint)Math.Min(UnreservedMana(conn), (long)conn.PlayerMana + devour.Mana);
        if (totem.GrantedId != 0)
            bytes.AddRange(InstanceProtocol.ActionStarted(totem.Id, totem.At.X, totem.At.Y, at.X, at.Y,
                totem.GrantedId, 0xffff, 0x0408));
        bytes.AddRange(InstanceProtocol.RemoveObject(corpseId));
        bytes.AddRange(InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerLife, conn.PlayerReservedLife));
        bytes.AddRange(InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerMana, (uint)ReservedMana(conn), pool: ManaPool));
        devour.NextUse = now + 1;
        Console.WriteLine($"[gateway] totem 0x{totem.Id:x} devours corpse 0x{corpseId:x} at ({at.X}, {at.Y}): "
            + $"+{devour.Life} life, +{devour.Mana} mana (player {conn.PlayerLife}/{conn.PlayerMaxLife})");
    }

    /// <summary>The taunting totem a monster at a point is drawn to: the nearest living one within
    /// <see cref="TotemRange"/> cells, or null. The monster AI reads this so its chase and its hit go to the totem
    /// instead of the player (Decoy Totem). Under the Monsters lock.</summary>
    internal static uint? TotemTauntLocked(ClientState conn, (int X, int Y) at, double now)
        => conn.Totems.Living(now).Where(t => t.Taunt && Dist(t.At, at) <= TotemRange)
            .OrderBy(t => (long)(t.At.X - at.X) * (t.At.X - at.X) + (long)(t.At.Y - at.Y) * (t.At.Y - at.Y))
            .Select(t => (uint?)t.Id).FirstOrDefault();

    /// <summary>The cell of a living totem, or null when that id is not (or no longer) one of them. The monster AI reads
    /// it to walk a taunted monster to the totem it goes for. Under the Monsters lock.</summary>
    internal static (int X, int Y)? TotemAtLocked(ClientState conn, uint id, double now)
        => conn.Totems.All.FirstOrDefault(t => t.Id == id && t.Alive(now))?.At;

    /// <summary>A monster's whole hit on a totem (a taunt's contact): its damage goes to the totem's Life, which is the
    /// redirect stat's 100% case (the wiki's Decoy Totem takes the hits it draws). At 0 the totem dies like any other;
    /// damage past its Life is lost, as a totem takes no overflow. Under the Monsters lock.</summary>
    internal static void TotemTakesMonsterHitLocked(ClientState conn, uint totemId, int damage, double now,
        IReadOnlyDictionary<uint, int> stats)
    {
        if (conn.Totems.All.FirstOrDefault(t => t.Id == totemId) is not { } totem || !totem.Alive(now)) return;
        var (_, left) = TotemState.Redirect(damage, 100, totem.Life);
        totem.Life = left;
        if (left == 0) { totem.DiedAt = now; TotemsDiedLocked(conn, now, stats); }
    }

    // A totem's use lands at its contact: every living hostile monster within the skill's radius of the point the use
    // was aimed at takes that skill's hit (the player's damage for it, as a placed trap's), mitigated as any hit and
    // with its ailments. The totem's kills give the player their experience but are not his for his on-kill effects.
    static void TotemStrikeLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        TotemState.Totem totem, TotemState.Strike strike, double now)
    {
        var player = CharacterPlayerStats(conn);
        foreach (var (id, m) in conn.Monsters.ToArray())
        {
            if (m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false }
                || Dist(m.Walk.Position(now), strike.Aim) > strike.Radius) continue;
            var rolled = strike.Hit.Roll(Random.Shared);
            var mitigated = DamageModel.Mitigate(rolled,
                type => MonsterHitResistance(m, type, default, now));
            int damage = MonsterTakesMitigated(m, mitigated, strike.Spell, now);
            if (damage <= 0) continue;
            // The lab's ledger sees the hit too: a totem's use is a damage site of its own (no player click produced
            // it), and without a record "did the totem's attack land?" has no answer. NoteTotemOutgoing keeps the
            // player sites' record shape (Kind "outgoing") and only changes the distance's origin, since the hit
            // comes from the totem and not from the player.
            uint lifeBefore = m.Life;
            if (ApplyMonsterHitLocked(conn, m, damage, allyAt: totem.At) is not { } hit) continue;
            NoteTotemOutgoing(conn, totem, id, m, rolled, damage, lifeBefore, now);
            if (hit.StageStarts) { CommitBossStage(remote, stream, ciphers, conn, id, m, now); hit = hit with { StageStarts = false }; }
            CommitAcceptedHitLocked(conn, id, m, hit, damage, now, "totem", playerKill: false);
            if (!m.Dead)
                m.Ailments.Inflict(mitigated, strike.Spell, m.MaxLife, now,
                    n => GolemStat(strike.Sources, n) + GolemStat(player, n), n => m.Effects[n], Random.Shared);
        }
    }

    // The totems as the client should see them, from the player tick and after a placement: the living ones use their
    // skill, the expired ones die, their Life (0x1b1), the dead ones' removal (0x2bd), and the aura buff on the player
    // (0x1c1 from the totem, 0x1c2) with his stats.
    /// <summary>The totems' life, removal and aura, without the uses they take: the spell totems' executor drives its
    /// own bytes, so a caller that only presents the totems' state does not send their actions again.</summary>
    internal static byte[]? TotemPresentation(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea) return null;
            var totems = conn.Totems;
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (totems.Expire(now).Count > 0) TotemsDiedLocked(conn, now, CharacterPlayerStats(conn));
            var bytes = new List<byte>();
            foreach (var t in totems.All)
            {
                uint life = t.DiedAt is null ? t.Life : 0;
                if (t.SentDeath || life == t.SentLife && life > 0) continue;
                bytes.AddRange(InstanceProtocol.LifeUpdate(t.Id, life));
                (t.SentLife, t.SentDeath) = (life, life == 0);
            }
            foreach (var t in totems.Remove(now))
            {
                conn.RemoveBlocker(t.Id);
                bytes.AddRange(InstanceProtocol.RemoveObject(t.Id));
            }
            uint source = conn.PlayerLife > 0 ? totems.AuraAt(conn.Walk.Position(now), now)?.Id ?? 0 : 0;
            if (source != totems.SentAuraTotem)
            {
                if (totems.SentAuraInstance != 0) bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, totems.SentAuraInstance));
                totems.SentAuraInstance = 0;
                // shortcut: the buff names the player as its source (the shape every server buff uses), not the totem and its
                // skill; send the totem once a live check shows the client takes a monster source for a player buff.
                if (totems.All.FirstOrDefault(t => t.Id == source) is { Aura: { } aura })
                    bytes.AddRange(InstanceEntities.AddBuff(PlayerId, totems.SentAuraInstance = conn.NextBuff++, aura.Row, PlayerId));
                totems.SentAuraTotem = source;
                bytes.AddRange(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
            }
            return bytes.Count == 0 ? null : [.. bytes];
        }
    }

    internal static byte[]? TotemPresentation(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea) return null;
            var totems = conn.Totems;
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (totems.Expire(now).Count > 0) TotemsDiedLocked(conn, now, CharacterPlayerStats(conn));
            var bytes = new List<byte>();
            foreach (var t in totems.Living(now)) TotemUseLocked(remote, stream, ciphers, conn, t, now, bytes);
            foreach (var t in totems.All)
            {
                uint life = t.DiedAt is null ? t.Life : 0;
                if (t.SentDeath || life == t.SentLife && life > 0) continue;
                bytes.AddRange(InstanceProtocol.LifeUpdate(t.Id, life));
                (t.SentLife, t.SentDeath) = (life, life == 0);
            }
            foreach (var t in totems.Remove(now))
            {
                conn.RemoveBlocker(t.Id);
                bytes.AddRange(InstanceProtocol.RemoveObject(t.Id));
            }
            uint source = conn.PlayerLife > 0 ? totems.AuraAt(conn.Walk.Position(now), now)?.Id ?? 0 : 0;
            if (source != totems.SentAuraTotem)
            {
                if (totems.SentAuraInstance != 0) bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, totems.SentAuraInstance));
                totems.SentAuraInstance = 0;
                // shortcut: the buff names the player as its source (the shape every server buff uses), not the totem and its
                // skill; send the totem once a live check shows the client takes a monster source for a player buff.
                if (totems.All.FirstOrDefault(t => t.Id == source) is { Aura: { } aura })
                    bytes.AddRange(InstanceEntities.AddBuff(PlayerId, totems.SentAuraInstance = conn.NextBuff++, aura.Row, PlayerId));
                totems.SentAuraTotem = source;
                bytes.AddRange(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
            }
            return bytes.Count == 0 ? null : [.. bytes];
        }
    }

    static Task SendTotems(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        Send(remote, stream, ciphers, [], null, atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || conn.Paused || conn.Closed || conn.LeavingArea) return null;
                var bytes = new List<byte>();
                TickSpellTotemsLocked(remote, stream, ciphers, conn, bytes);
                bytes.AddRange(TotemPresentation(remote, stream, ciphers, conn) ?? []);
                return bytes.Count == 0 ? null : [.. bytes];
            }
        });
}
