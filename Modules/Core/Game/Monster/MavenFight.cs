using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;

/// <summary>
/// The two halves of The Maven's encounter (MavenBoss "Absence of Mercy and Empathy", level 84) share one shape: the
/// hits a cast still owes the fight and a cast whose clip and whose numbers can live in different effect rows (her
/// action-script rows animate the cast and the row they fire carries the damage).
/// <para>Every timed hit is committed per player the geometry covers (<see cref="Hit"/>), never to the connection that
/// runs the fight: the instance's runner plays the steps, so a hit committed for it alone would spare every other
/// player standing in the same blast.</para>
/// </summary>
abstract class MavenEncounterFight(uint id, SpawnedMonster boss, ClientState conn) : AtlasBossFight(id, boss, conn)
{
    // What a cast already owes the fight: a slam's circle, a beam's corridor, a barrage's impact. From == To makes it a
    // circle, otherwise it is the corridor between them (the Elder's and Sirus' own convention).
    protected readonly List<(double At, MonsterAttack Attack, (int X, int Y) From, (int X, int Y) To, double Radius)>
        Pending = [];

    /// <summary>The clip and the numbers of one of the boss's skills live in different rows here: an action-script row
    /// (the Maven's EAS* casts) is what the client animates, the row it fires is what lands. The clip's own cast time
    /// keeps the boss busy; the hit row gives the damage, the reach and the projectile.</summary>
    protected MonsterAttack Cast(ClientState conn, int clipEffect, int hitEffect)
    {
        var hit = Attack(conn, hitEffect);
        return hit with
        {
            SkillId = AtlasBosses.Skill(Variety, clipEffect),
            Seconds = Math.Max(0.3, SkillsFor(conn).CastTimeMs(clipEffect) / 1000.0),
        };
    }

    /// <summary>Lands the hits a cast still owed: every player the geometry covers takes its own hit.</summary>
    protected async Task Land(double now)
    {
        foreach (var hit in Pending.Where(h => h.At <= now).ToArray())
        {
            Pending.Remove(hit);
            foreach (var player in Watchers())
                if (InReach(player.Walk.Position(now), hit.From, hit.To, hit.Radius))
                    await Hit(player, hit.Attack, hit.From);
        }
    }

    // A hit's geometry: a circle when From == To, otherwise the corridor between them.
    static bool InReach((int X, int Y) at, (int X, int Y) from, (int X, int Y) to, double radius) =>
        from == to ? Distance(at, from) <= radius : DistanceToSegment(at, from, to) <= radius;

    static double DistanceToSegment((int X, int Y) at, (int X, int Y) from, (int X, int Y) to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double length = dx * dx + dy * dy;
        if (length <= 0) return Distance(at, from);
        double t = Math.Clamp(((at.X - from.X) * dx + (at.Y - from.Y) * dy) / length, 0, 1);
        return Distance(at, ((int)Math.Round(from.X + t * dx), (int)Math.Round(from.Y + t * dy)));
    }
}

/// <summary>
/// The Maven (MonsterVarieties <c>Metadata/Monsters/MavenBoss/TheMaven</c>, WorldAreas MavenBoss "Absence of Mercy and
/// Empathy", level 84). Her GrantedEffectsKeys are the kit played here: the lightning shot she spams, her circle slam,
/// the cold cannon, the super fire projectile with the impact it lands, the laser barrage of six invisible targets and
/// her two teleports (6810 TeleportMaven, 6811 TeleportMavenToCentre — a teleport played as an action lands that
/// action's contact too, so the fight moves her and plays the clip as art, the way Sirus' blink does). Her three
/// memory-game rows are not played: that is a minigame of platforms and clones the server does not model.
/// <para>Damage comes from each effect's own row (<see cref="AtlasBosses.MavenDamage"/>): the last per-level row's
/// FloatStatsValues are the fraction of the variety's own damage, and the row's own stat names the element (her shot
/// 0.75-2.25 lightning, the slam 1.6-2.4 physical converted in thirds to fire/cold/lightning, the cannon 0.8-1.2 cold,
/// the super fire impact 1.6-2.4 fire and a laser beam 1.6-2.4 fire). The impact's row resolves to a single point of
/// damage, so the projectile is a launcher and the impact carries the hit.</para>
/// <para>She never swings a weapon: her record's own skill is the shot, and the rotation is what keeps her at range — a
/// blink out of a player's face, a blink after one that runs. The cooldowns below are design values chosen for a level
/// 84 arena, not figures the client's tables carry.</para>
/// </summary>
sealed class MavenFight(uint id, SpawnedMonster boss, ClientState conn) : MavenEncounterFight(id, boss, conn)
{
    protected override string Name => "[Maven]";
    protected override ushort ImmunityRow => AtlasBosses.MavenImmunityRow;
    protected override string ShieldArt => AtlasBosses.MavenShieldArt;
    protected override string ShieldOffArt => AtlasBosses.MavenShieldOffArt;

    // Where a phase change puts her: the arena's own middle marker, never the layout's spawn (the map entry).
    (int X, int Y)? stage;
    protected override (int X, int Y) Stage(ClientState conn) => stage ??= ArenaCentre(conn, boss);

    double nextShot, nextSlam, nextCannon, nextFire, nextLaser, nextBlink, nextChase;
    bool intermission;
    int waves;
    double waveAt;

    protected override Task Ticks(ClientState conn, double now) => Land(now);

    protected override async Task Act(ClientState conn, double now)
    {
        var watchers = Watchers();
        if (watchers.Length == 0) return;
        var pos = boss.Walk.Position(now);
        var target = watchers.MinBy(p => Distance(pos, p.Walk.Position(now)))!;
        var player = target.Walk.Position(now);
        double distance = Distance(pos, player);

        // A player in her face is blinked away from; one that runs out of her reach is blinked after. Both are the
        // teleport's art at either end, no action: a teleport that played an attack would also hit.
        if (distance < 14 && now >= nextBlink)
        {
            nextBlink = now + 9;
            await Blink(conn, player, now, away: true);
            return;
        }
        if (distance > 50 && now >= nextBlink)
        {
            nextBlink = now + 8;
            await Blink(conn, player, now, away: false);
            return;
        }
        if (now >= nextLaser)
        {
            nextLaser = now + 24;
            await LaserBarrage(conn, player, now);
            return;
        }
        if (now >= nextFire)
        {
            nextFire = now + 17;
            await SuperFire(conn, player, now);
            return;
        }
        if (now >= nextCannon)
        {
            nextCannon = now + 11;
            await Cannon(conn, now);
            return;
        }
        // The slam needs a player near her; with nobody there its cooldown is left standing, so it fires as soon as one
        // steps in instead of on the next fourteen seconds.
        if (now >= nextSlam && distance <= 24)
        {
            nextSlam = now + 13;
            await Slam(conn, now);
            return;
        }
        // Her record's own skill: the lightning shot the ordinary swing played before this controller existed.
        var basic = Attack(conn, AtlasBosses.MavenProjectileEffect);
        if (distance <= basic.Range && now >= nextShot)
        {
            nextShot = now + basic.Seconds + 0.4;
            Swing(conn, AtlasBosses.MavenProjectileEffect);
            return;
        }
        if (distance > basic.Range && now >= nextChase)
        {
            nextChase = now + 0.75;
            double fraction = Math.Clamp((distance - basic.Range + 2) / distance, 0, 1);
            var chase = ((int)Math.Round(pos.X + (player.X - pos.X) * fraction),
                (int)Math.Round(pos.Y + (player.Y - pos.Y) * fraction));
            await Move(conn, conn.Layout.Near(chase), now);
        }
    }

    /// <summary>The circle slam (MavenCircleSlam 11725 through the MavenEASCircleSlam clip 11741, a 3 s cast): the ring
    /// of damage around her, wide enough that a player standing next to her has to run out of it.</summary>
    async Task Slam(ClientState conn, double now)
    {
        var pos = boss.Walk.Position(now);
        await Effect(conn, pos, AtlasBosses.MavenSlamArt, 3.6, 1.6f);
        var attack = Cast(conn, AtlasBosses.MavenEASCircleSlamEffect, AtlasBosses.MavenCircleSlamEffect);
        Pending.Add((now + attack.Seconds + 0.2, attack, pos, pos, 16));
        SwingWith(conn, attack, AtlasBosses.MavenEASCircleSlamEffect);
    }

    /// <summary>The cold cannon (MavenCannonColdProjectile 11749 through the MavenEASCannon clip 11740): a fan of
    /// three cold shots, Projectiles.dat' MavenCannonColdProjectile flying at 1500 units a second with a 4 unit
    /// collision radius.</summary>
    async Task Cannon(ClientState conn, double now)
    {
        var from = boss.Walk.Position(now);
        await Effect(conn, from, AtlasBosses.MavenLaserHitArt, 1.6, 1.2f);
        var attack = Cast(conn, AtlasBosses.MavenEASCannonEffect, AtlasBosses.MavenCannonColdProjectileEffect);
        if (attack.Projectile is { } shot)
            attack = attack with { Projectile = shot with { Count = 3, SpreadRadians = 0.5 } };
        SwingWith(conn, attack, AtlasBosses.MavenEASCannonEffect);
    }

    /// <summary>The super fire projectile (11750, a 3 s cast, 750 units a second): the shot is slow and the impact it
    /// lands (11751) is what hurts, 1.6-2.4 of her damage as fire with a doubled ignite.</summary>
    async Task SuperFire(ClientState conn, (int X, int Y) player, double now)
    {
        await Effect(conn, player, AtlasBosses.MavenFireImpactArt, 3.6, 1.6f);
        var impact = Attack(conn, AtlasBosses.MavenSuperFireProjectileImpactEffect);
        var attack = impact with
        {
            SkillId = AtlasBosses.Skill(Variety, AtlasBosses.MavenSuperFireProjectileEffect),
            Seconds = Math.Max(0.3, SkillsFor(conn).CastTimeMs(AtlasBosses.MavenSuperFireProjectileEffect) / 1000.0),
            Ranged = true,
            Projectile = AtlasBosses.ProjectileFor(conn, AtlasBosses.MavenSuperFireProjectileEffect),
        };
        SwingWith(conn, attack, AtlasBosses.MavenSuperFireProjectileEffect);
    }

    /// <summary>The laser barrage (MavenEASLaserBarrage 11739, a 5 s cast): the real fight's six invisible targets
    /// (Metadata/Monsters/InvisibleFire/MavenLaserBarrageTarget1-6, each carrying the beam impact 1872) fire at once.
    /// The controller does not spawn those six actors; it draws their beams from six points around the arena onto the
    /// ground the player stands on, so the barrage is dodged by walking out of the lines.</summary>
    async Task LaserBarrage(ClientState conn, (int X, int Y) player, double now)
    {
        var centre = boss.Walk.Position(now);
        var attack = Cast(conn, AtlasBosses.MavenLaserBarrageEffect, AtlasBosses.MavenLaserBeamEffect);
        double towards = Math.Atan2(player.Y - centre.Y, player.X - centre.X);
        var aim = conn.Layout.Near(player);
        for (int i = 0; i < 6; i++)
        {
            double angle = towards + Math.Tau * i / 6;
            var from = conn.Layout.Near((centre.X + (int)Math.Round(Math.Cos(angle) * 34),
                centre.Y + (int)Math.Round(Math.Sin(angle) * 34)));
            await Effect(conn, from, AtlasBosses.MavenLaserHitArt, 2.2, 1.4f);
            Pending.Add((now + attack.Seconds + 0.3, attack, from, aim, 4));
        }
        SwingWith(conn, attack, AtlasBosses.MavenLaserBarrageEffect);
    }

    /// <summary>Her teleports (6810/6811): the art plays at both ends, the walk estimate and the client's actor move,
    /// and no action goes out — a teleport that played an attack would land that attack's contact.</summary>
    async Task Blink(ClientState conn, (int X, int Y) player, double now, bool away)
    {
        var from = boss.Walk.Position(now);
        double angle = Math.Atan2(from.Y - player.Y, from.X - player.X);
        double distance = away ? 22 : 18;
        var to = conn.Layout.Near((from.X + (int)Math.Round(Math.Cos(angle) * distance),
            from.Y + (int)Math.Round(Math.Sin(angle) * distance)));
        await Effect(conn, from, AtlasBosses.MavenTeleportArt, 1);
        await Teleport(conn, to, now);
        await Effect(conn, to, AtlasBosses.MavenTeleportOffArt, 1);
    }

    /// <summary>A threshold's intermission: her stasis (the arena's centrepiece lights up), a blink to the arena's
    /// middle and three waves of her kit around it before she can be hurt again.</summary>
    protected override async Task Intermission(ClientState conn, double now)
    {
        if (!intermission)
        {
            intermission = true;
            waves = 0;
            await Shield(conn, true);
            await Teleport(conn, Stage(conn), now);
            waveAt = now + 1.4;
            busyUntil = now + 1.4;
            return;
        }
        if (now < waveAt) return;
        if (waves == 3)
        {
            intermission = false;
            await Shield(conn, false);
            EndIntermission();
            nextShot = now + 2;
            busyUntil = now + 1;
            return;
        }
        waves++;
        var from = boss.Walk.Position(now);
        var player = Watchers().Select(p => p.Walk.Position(now)).DefaultIfEmpty(from).MinBy(p => Distance(from, p));
        switch (waves)
        {
            case 1: await Cannon(conn, now); break;
            case 2: await LaserBarrage(conn, player, now); break;
            default: await Slam(conn, now); break;
        }
        waveAt = now + 2.4;
        busyUntil = now + 1.8;
    }
}

/// <summary>
/// The Nucleus (MonsterVarieties <c>Metadata/Monsters/MavenBoss/MavenBrainBoss</c>): the brain The Maven's death forms
/// in the same arena (MapBossFights holds it back until she dies, QuestEngine spawns it where she fell with the four
/// orbit cores around it). Its variety carries no boss bar of its own (<c>BossHealthBar</c> false): the fight gives it
/// the arena's bar, because it is the encounter's second phase and its three thresholds have to be readable.
/// <para>It is the same scripted fight as hers: the charge beam, the ion cannon, the beam blast and the projectile
/// barrages, each played from its own row by <see cref="AtlasBosses.MavenDamage"/>.</para>
/// <para>It never walks. The real one sits in the middle of the arena and turns its beams, so its rotation is the only
/// movement; a blink to the arena's middle joins a phase change, never the map entry.</para>
/// <para>The cooldowns are design values for the level 84 arena, not figures the client's tables carry.</para>
/// </summary>
sealed class NucleusFight(uint id, SpawnedMonster boss, ClientState conn) : MavenEncounterFight(id, boss, conn)
{
    protected override string Name => "[Nucleus]";
    protected override ushort ImmunityRow => AtlasBosses.MavenImmunityRow;
    protected override string ShieldArt => AtlasBosses.NucleusShieldArt;
    protected override string ShieldOffArt => AtlasBosses.NucleusShieldOffArt;

    (int X, int Y)? stage;
    protected override (int X, int Y) Stage(ClientState conn) => stage ??= ArenaCentre(conn, boss);

    double nextBarrage, nextDecel, nextBig, nextBeam, nextCannon, nextBlast;
    bool intermission;
    int waves;
    double waveAt;

    protected override Task Ticks(ClientState conn, double now) => Land(now);

    protected override async Task Act(ClientState conn, double now)
    {
        var watchers = Watchers();
        if (watchers.Length == 0) return;
        var pos = boss.Walk.Position(now);
        var player = watchers.MinBy(p => Distance(pos, p.Walk.Position(now)))!.Walk.Position(now);

        if (now >= nextBlast)
        {
            nextBlast = now + 21;
            await BeamBlast(conn, player, now);
            return;
        }
        if (now >= nextCannon)
        {
            nextCannon = now + 19;
            await IonCannon(conn, player, now);
            return;
        }
        if (now >= nextBeam)
        {
            nextBeam = now + 14;
            await ChargeBeam(conn, player, now);
            return;
        }
        if (now >= nextBig)
        {
            nextBig = now + 16;
            await BigBarrage(conn, player, now);
            return;
        }
        if (now >= nextDecel)
        {
            nextDecel = now + 9;
            await Decelerating(conn, player, now);
            return;
        }
        // Its record's own skill is the small barrage: the MPS row carries the shot itself (750 units a second, 3 unit
        // collision radius) where the spell row only names it.
        if (now >= nextBarrage)
        {
            nextBarrage = now + 6;
            Swing(conn, AtlasBosses.NucleusSmallProjectileBarrageMpsEffect);
        }
    }

    /// <summary>The charge beam (MavenBrainChargeBeam 11723, a 3 s cast): a corridor from the brain onto the player,
    /// its physical damage converted in thirds to fire, cold and lightning.</summary>
    async Task ChargeBeam(ClientState conn, (int X, int Y) player, double now)
    {
        var (from, to) = Corridor(conn, player, now, 34);
        await Effect(conn, from, AtlasBosses.NucleusBeamArt, 4.4, 1.6f);
        var attack = Attack(conn, AtlasBosses.NucleusChargeBeamEffect);
        Pending.Add((now + attack.Seconds * 0.8, attack, from, to, 5.5));
        SwingWith(conn, attack, AtlasBosses.NucleusChargeBeamEffect);
    }

    /// <summary>The ion cannon (MavenBrainIonCannon 11724, a 6 s cast): the wide blast under the player, lightning and
    /// cold at once (0.4-0.6 of its damage each).</summary>
    async Task IonCannon(ClientState conn, (int X, int Y) player, double now)
    {
        var at = conn.Layout.Near(player);
        await Effect(conn, at, AtlasBosses.NucleusWideExplosionArt, 6.8, 2f);
        var attack = Attack(conn, AtlasBosses.NucleusIonCannonEffect);
        Pending.Add((now + attack.Seconds * 0.9, attack, at, at, 12));
        SwingWith(conn, attack, AtlasBosses.NucleusIonCannonEffect);
    }

    /// <summary>The beam blast (MavenBrainBeamBlast 11726, a 4 s cast): a wide corridor, physical converted in thirds
    /// like the charge beam's.</summary>
    async Task BeamBlast(ClientState conn, (int X, int Y) player, double now)
    {
        var (from, to) = Corridor(conn, player, now, 30);
        await Effect(conn, from, AtlasBosses.NucleusBeam2Art, 5.4, 1.8f);
        var attack = Attack(conn, AtlasBosses.NucleusBeamBlastEffect);
        Pending.Add((now + attack.Seconds * 0.85, attack, from, to, 9));
        SwingWith(conn, attack, AtlasBosses.NucleusBeamBlastEffect);
    }

    /// <summary>The large projectile barrage (MavenBrainLargeProjectileBarrageMPS 11731 through the impact row 11733):
    /// two slow shots, their 300 units a second the Projectiles.dat row's own, whose landing carries the impact's
    /// lightning 0.5-1.5.</summary>
    async Task BigBarrage(ClientState conn, (int X, int Y) player, double now)
    {
        var hit = Attack(conn, AtlasBosses.NucleusLargeProjectileImpactEffect);
        var attack = hit with
        {
            SkillId = AtlasBosses.Skill(Variety, AtlasBosses.NucleusLargeProjectileBarrageMpsEffect),
            Seconds = Math.Max(0.3, SkillsFor(conn).CastTimeMs(AtlasBosses.NucleusLargeProjectileBarrageMpsEffect) / 1000.0),
            Ranged = true,
            Projectile = AtlasBosses.ProjectileFor(conn, AtlasBosses.NucleusLargeProjectileBarrageMpsEffect),
        };
        if (attack.Projectile is { } shot)
            attack = attack with { Projectile = shot with { Count = 2, SpreadRadians = 0.35 } };
        await Effect(conn, player, AtlasBosses.NucleusBigProjectileImpactArt, 4.4, 1.6f);
        SwingWith(conn, attack, AtlasBosses.NucleusLargeProjectileBarrageMpsEffect);
    }

    /// <summary>The decelerating projectile (MavenBrainDeceleratingProjectile 11752, cold 0.8-1.2): its own stat set
    /// halves its speed, so the shot hangs in the air where it was aimed.</summary>
    async Task Decelerating(ClientState conn, (int X, int Y) player, double now)
    {
        await Effect(conn, conn.Layout.Near(player), AtlasBosses.NucleusVoidZoneArt, 2.4, 1.4f);
        Swing(conn, AtlasBosses.NucleusDeceleratingProjectileEffect);
    }

    /// <summary>A corridor from the brain to the player: where the beam points and where its far end lands.</summary>
    ((int X, int Y) From, (int X, int Y) To) Corridor(ClientState conn, (int X, int Y) player, double now, int length)
    {
        var from = boss.Walk.Position(now);
        double angle = Math.Atan2(player.Y - from.Y, player.X - from.X);
        var to = conn.Layout.Near((from.X + (int)Math.Round(Math.Cos(angle) * length),
            from.Y + (int)Math.Round(Math.Sin(angle) * length)));
        return (from, to);
    }

    /// <summary>A threshold's intermission: the beam shield, the move to the arena's middle and three barrage fans
    /// around it while the brain cannot be hurt.</summary>
    protected override async Task Intermission(ClientState conn, double now)
    {
        if (!intermission)
        {
            intermission = true;
            waves = 0;
            await Shield(conn, true);
            await Teleport(conn, Stage(conn), now);
            waveAt = now + 1.4;
            busyUntil = now + 1.4;
            return;
        }
        if (now < waveAt) return;
        if (waves == 3)
        {
            intermission = false;
            await Shield(conn, false);
            EndIntermission();
            nextBarrage = now + 2;
            busyUntil = now + 1;
            return;
        }
        waves++;
        var from = boss.Walk.Position(now);
        var player = Watchers().Select(p => p.Walk.Position(now)).DefaultIfEmpty(from).MinBy(p => Distance(from, p));
        switch (waves)
        {
            case 1: await ChargeBeam(conn, player, now); break;
            case 2: await BigBarrage(conn, player, now); break;
            default: await IonCannon(conn, player, now); break;
        }
        waveAt = now + 2.4;
        busyUntil = now + 1.8;
    }
}
