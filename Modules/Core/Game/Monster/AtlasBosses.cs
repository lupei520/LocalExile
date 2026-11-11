using System.Runtime.CompilerServices;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;

/// <summary>
/// The scripted pinnacle bosses: the Shaper and the Elder (WorldAreas MapWorldsShapersRealm at level 84 and
/// MapWorldsElderArena / MapWorldsElderArenaUber at 83/84), Sirus (AtlasExilesBoss5, the Eye of the Storm, level 84),
/// and The Maven with the Nucleus her death forms (MavenBoss "Absence of Mercy and Empathy", level 84). Their
/// MonsterVarieties rows list the whole kit in GrantedEffectsKeys, but the ordinary monster AI played one of those ids
/// as a plain swing, so the bosses only auto-attacked. <see cref="ShaperFight"/>, <see cref="ElderFight"/>,
/// <see cref="SirusFight"/>, <see cref="MavenFight"/> and <see cref="NucleusFight"/> play the kit instead; this class
/// holds what all of them need: which varieties they own, the skill ids, the per-effect <see cref="MonsterAttack"/>
/// (damage straight from the effect's own stat set) and the actor skill list the client needs before it can play a
/// single clip.
/// <para>Skill ids: 0x4000+ per boss, never the player's gem range (0x0103..0x0142). The client's skill manager resolves
/// an id globally, so reusing a gem id for a monster action makes it start that gem's action instead (KingInMists.cs:
/// the client crashed at the action, 0xc0000005). The three bosses get disjoint ranges (0x4000 the Shaper, 0x4010 the
/// Elder, 0x4020 Sirus) because the Shaper and the Elder stand in MapWorldsElderArenaUber together.</para>
/// <para>Art: every .ao path below was checked against the client's metadata (data/game-files.zip, the whole
/// metadata/effects/spells/monsters_effects/atlasofworldsbosses tree).</para>
/// </summary>
static class AtlasBosses
{
    internal const string Shaper = "Metadata/Monsters/AtlasBosses/TheShaperBoss";
    // The Shaper as the Uber Elder encounter brings him (MapWorldsElderArenaUber): no GrantedEffectsKeys of his own and
    // no boss bar, standing beside TheElderUber.
    internal const string ShaperUberElder = "Metadata/Monsters/AtlasBosses/TheShaperBossUberElder";
    internal const string Elder = "Metadata/Monsters/AtlasBosses/TheElder/TheElder";
    internal const string ElderUber = "Metadata/Monsters/AtlasBosses/TheElder/TheElderUber";
    // Sirus, Awakener of Worlds (AtlasExilesBoss5). The Gauntlet and the standalone arenas are the same fight; the
    // throne is the row he sits on when the arena is opened (see the phase note below), and the apparition/wild rows are
    // other encounters with kits of their own.
    internal const string Sirus = "Metadata/Monsters/AtlasExiles/AtlasExile5";
    internal const string SirusThrone = "Metadata/Monsters/AtlasExiles/AtlasExile5Throne";
    internal const string SirusGauntlet = "Metadata/Monsters/AtlasExiles/AtlasExile5Gauntlet";
    internal const string SirusStandalone = "Metadata/Monsters/AtlasExiles/AtlasExile5Standalone";
    internal const string SirusStandaloneMirage = "Metadata/Monsters/AtlasExiles/AtlasExile5StandaloneMirage";

    // GrantedEffects rows (MonsterVarieties GrantedEffectsKeys; poedb names the same skills).
    internal const int ShaperMeleeEffect = 481;
    internal const int CelestialBeamEffect = 6093;
    internal const int AcceleratingProjectilesEffect = 6106;
    internal const int FlickerSlamEffect = 6091;
    internal const int RetreatEffect = 6089;
    internal const int BulletHellEffect = 6111;
    internal const int ElderMeleeEffect = 406;
    internal const int SiphonEffect = 9058;
    internal const int SlammingTentaclesEffect = 9062;
    internal const int ExpandingNovaEffect = 9066;
    internal const int DoomProjectileEffect = 9351;
    internal const int ProjectileSpellcastEffect = 9330;
    // Sirus' kit (the Orion effects): the beam blast he spams, the corridor "Die" beam, the five circle-maze blasts of
    // one sweep, the multi-beam barrage, the clone beam and the meteor arena with the ground it leaves burning. His two
    // teleports (TeleportOrion 6809, AtlasOrionTeleportToCentre 10991) are not in it: a teleport played as an action also
    // lands that action's contact, so the fight moves him and plays the clip as art instead. The standalone varieties do
    // not grant those two anyway.
    internal const int SirusBeamBlastEffect = 7008;
    // The Maven's kit (MavenBoss "Absence of Mercy", level 84) and her Nucleus' (the brain that forms when she dies).
    // The Maven: her basic lightning shot, the circle slam, the cold cannon, the super fire projectile with its
    // impact, the laser barrage and its six invisible targets, the three memory-game rows and the two teleports
    // (6810/6811) are not in the kit: a teleport played as an action also lands that action's contact, so the
    // fight moves her and plays the clip as art instead.
    internal const int MavenProjectileEffect = 11748;
    internal const int MavenCircleSlamEffect = 11725;
    internal const int MavenCannonColdProjectileEffect = 11749;
    internal const int MavenSuperFireProjectileEffect = 11750;
    internal const int MavenSuperFireProjectileImpactEffect = 11751;
    internal const int MavenLaserBarrageEffect = 11739;
    // The Nucleus' kit: the charge beam, the ion cannon, the beam blast, the three barrages (their MPS rows carry
    // the shots themselves), the large barrage's impact and the decelerating projectile.
    internal const int NucleusChargeBeamEffect = 11723;
    internal const int NucleusIonCannonEffect = 11724;
    internal const int NucleusBeamBlastEffect = 11726;
    internal const int NucleusProjectileBlastMpsEffect = 11730;
    internal const int NucleusLargeProjectileBarrageMpsEffect = 11731;
    internal const int NucleusSmallProjectileBarrageMpsEffect = 11732;
    internal const int NucleusLargeProjectileImpactEffect = 11733;
    internal const int NucleusDeceleratingProjectileEffect = 11752;
    // The Nucleus' record's own shot (the plain AI's basic for it) and the barrage's explosion.
    internal const int NucleusSmallProjectileBarrageEffect = 11729;
    internal const int NucleusProjectileBarrageExplodeEffect = 11756;
    // The Maven's three action-script rows (MavenEAS*): their stat sets carry no damage at all, they are the clip the
    // client animates (animations 35/34/33 and 3/3.5/5 s casts) and the real fight's AI is what fires the hit rows
    // below. The controller plays the clip for the cast time and lands the hit row's own numbers, the way the Shaper's
    // flicker slam plays its teleport clip and lands the slam.
    internal const int MavenEASCircleSlamEffect = 11741;
    internal const int MavenEASCannonEffect = 11740;
    // The six invisible laser targets' beam impact (Metadata/Monsters/InvisibleFire/MavenLaserBarrageTarget1-6 carry
    // this one effect; their own damage multiplier is 100 against The Maven's 150).
    internal const int MavenLaserBeamEffect = 1872;
    internal const int SirusCorridorBlastEffect = 10948;
    internal const int SirusCircleMazeEffect = 10949;
    internal const int SirusCircleMaze2Effect = 10950;
    internal const int SirusCircleMaze3Effect = 10951;
    internal const int SirusCircleMaze4Effect = 10952;
    internal const int SirusCircleMaze5Effect = 10953;
    internal const int SirusMultiBeamEffect = 8098;
    internal const int SirusMultiBeam2Effect = 8099;
    internal const int SirusCloneBeamEffect = 7031;
    internal const int SirusMeteorEffect = 10141;
    internal const int SirusMeteorGroundEffect = 10945;
    internal const int SirusBeamBombEffect = 10973;

    // ActionTypes HASH16 & 0x3fff: the basic Move. A boss that has to close the distance walks it, like the King.
    internal const ushort MoveSkill = 0x2909;

    // BuffDefinitions: the bosses' own intermission shields (uber_shaper_immune_buff / uber_elder_immune_buff), i.e. the
    // immunity icon the client shows for that phase, not the generic cannot_be_damaged row.
    internal const ushort ShaperImmunityRow = 1028, ElderImmunityRow = 1027;
    // atlas_orion_immunity: the "Protective Shield" Sirus hides behind while his storms take the arena.
    internal const ushort SirusImmunityRow = 1464;
    // The Maven's own "maven_boss_stasis" row (1696) is invisible and carries no stats at all, so it cannot grey her
    // out in the client; the generic invisible cannot_be_damaged row (base_cannot_be_damaged alone, the stat the
    // Shaper's, the Elder's and Sirus' rows carry too) is what shields her and the Nucleus.
    internal const ushort MavenImmunityRow = 350;

    const string ShaperArt = "Metadata/Effects/Spells/monsters_effects/atlasofworldsbosses/theshaper/";
    const string ElderArt = "Metadata/Effects/Spells/monsters_effects/atlasofworldsbosses/elder/";
    internal const string CelestialBeamArt = ShaperArt + "celestialbeam/rig.ao";
    internal const string ShaperShieldArt = ShaperArt + "immune_shield/immune_shield.ao";
    internal const string ShaperShieldOffArt = ShaperArt + "immune_shield/immune_shield_off.ao";
    internal const string FlickerSlamAppearArt = ShaperArt + "teleport_slam/teleport_appear.ao";
    internal const string FlickerSlamDisappearArt = ShaperArt + "teleport_slam/teleport_disappear.ao";
    internal const string FlickerSlamArt = ShaperArt + "teleport_slam/teleport_slam.ao";
    internal const string ShaperTeleportArt = ShaperArt + "instant_teleport/instant_teleport.ao";
    internal const string BulletHellCastArt = ShaperArt + "bullet_hell/cast/bullet_hell_cast.ao";
    internal const string ElderShieldArt = ElderArt + "immune_shield/immune_shield.ao";
    internal const string ElderShieldOffArt = ElderArt + "immune_shield/immune_shield_off.ao";
    internal const string ElderSiphonArt = ElderArt + "siphon/siphon_cone.ao";
    internal const string ElderNovaArt = ElderArt + "nova/nova.ao";
    internal const string ElderDoomArt = ElderArt + "doom/on_act/on_act.ao";
    internal const string ElderDoomHitArt = ElderArt + "doom/doom_hit/doomhit.ao";
    internal const string ElderTentaclePortalArt = ElderArt + "tentacle_portal/tentacle_portal.ao";
    internal const string ElderTentacleSlamArt = ElderArt + "tentacle_portal/slam_impact.ao";
    internal const string ElderTeleportPlayerArt = ElderArt + "teleport/player/warpslam.ao";

    const string SirusArt = "Metadata/Effects/Spells/monsters_effects/atlasexiles/orion/";
    internal const string SirusBeamBlastArt = SirusArt + "spam_beam.ao";
    internal const string SirusCorridorArt = SirusArt + "corridor_nuke.ao";
    internal const string SirusNukeBeamArt = SirusArt + "nuke_beam_blast.ao";
    internal const string SirusCarpetArt = SirusArt + "death_carpeting.ao";
    internal const string SirusTripleBeamArt = SirusArt + "triplebeam/triple_beam_01.ao";
    internal const string SirusTripleBeam2Art = SirusArt + "triplebeam/triple_beam_02.ao";
    internal const string SirusMeteorArt = SirusArt + "meteor/meteor_150.ao";
    internal const string SirusMeteorImpactArt = SirusArt + "destruction_assets/meteors/impact_medium.ao";
    internal const string SirusMeteorGroundArt = SirusArt + "destruction_assets/meteors/meteor_ground.ao";
    internal const string SirusShieldArt = SirusArt + "immunity_shield.ao";
    // No separate "shield off" .ao for Orion: the short warp-down flash plays when it drops.
    internal const string SirusShieldOffArt = SirusArt + "warp_down.ao";
    internal const string SirusTeleportArt = SirusArt + "teleporting/orion_teleport.ao";
    internal const string SirusDoomGeyserArt = SirusArt + "doom_geyser.ao";

    // The Maven's and the Nucleus' art (the whole metadata/effects/spells/monsters_effects/atlasofworldsbosses/maven
    // tree of the client's metadata was checked).
    const string MavenArt = "Metadata/Effects/Spells/monsters_effects/atlasofworldsbosses/maven/";
    // Her two blinks are art at both ends, the way Sirus' teleport is: a teleport played as an action would land that
    // action's contact.
    internal const string MavenTeleportArt = MavenArt + "envoy/act_teleport_in.ao";
    internal const string MavenTeleportOffArt = MavenArt + "envoy/act_teleport_out.ao";
    // Her stasis: the arena's centrepiece lights up while she cannot be hurt and goes out when the phase ends.
    internal const string MavenShieldArt = MavenArt + "centrepiece/centrepiece.ao";
    internal const string MavenShieldOffArt = MavenArt + "centrepiece/centrepiece_off.ao";
    internal const string MavenSlamArt = MavenArt + "spellcast_aoe_01/aoe.ao";
    internal const string MavenFireImpactArt = MavenArt + "cannon_01/fire_projectiles/aoe_impact.ao";
    internal const string MavenLaserHitArt = MavenArt + "carpet_bomb_01/eyelaser_aoe.ao";
    // The Nucleus: its beam shield for an intermission, the rectangles a charge beam and a beam blast leave, the
    // explosion the ion cannon's wide blast is, and the impact and the void ground of its barrages.
    internal const string NucleusShieldArt = MavenArt + "brain/beam_shield/brainbeamshield.ao";
    internal const string NucleusShieldOffArt = MavenArt + "brain/center_explosion/arena_explosion.ao";
    internal const string NucleusBeamArt = MavenArt + "brain/rectangle_beam/rectbeam.ao";
    internal const string NucleusBeam2Art = MavenArt + "brain/rectangle_beam2/rectbeam.ao";
    internal const string NucleusWideExplosionArt = MavenArt + "brain/arena_wide_explosion/wideexplosion.ao";
    internal const string NucleusBigProjectileImpactArt = MavenArt + "brain/projectile2/big_projectile_impact.ao";
    internal const string NucleusVoidZoneArt = MavenArt + "brain/void_zone/rig.ao";

    // Projectiles.dat rows for the shots no granted effect names (its monster_projectile_variation stat): the Shaper's
    // bullet-hell ring (375 units/s, collision radius 3) and the Elder's doom shot (600, radius 2), as Projectiles.dat
    // lists them. Speed is the row's own figure, the one ProjectileSpeed.CellsPerSecond reads.
    internal static SkillCatalog.MonsterProjectileInfo BulletHellShot { get; } =
        new("Metadata/Projectiles/ShaperBulletHellProjectile", 375, false, 3) { Count = 12, SpreadRadians = Math.Tau };
    internal static SkillCatalog.MonsterProjectileInfo DoomShot { get; } =
        new("Metadata/Projectiles/ElderProjectileDoom", 600, false, 2);

    internal static bool IsShaper(MonsterCatalog.Variety? variety) => variety?.Id is Shaper or ShaperUberElder;
    internal static bool IsElder(MonsterCatalog.Variety? variety) => variety?.Id is Elder or ElderUber;
    // AtlasExilesBoss5 is a phased arena for MapBossFights: WorldAreas lists [throne, walking], the walking row is the
    // held-back last boss, so a freshly opened arena contains only the throne. Both are Sirus and both fight: the throne
    // is the stage the player walks in on, and killing him is what forms the walking row and his four storms.
    internal static bool IsSirus(MonsterCatalog.Variety? variety) => variety?.Id is Sirus or SirusThrone or SirusGauntlet
        or SirusStandalone or SirusStandaloneMirage;

    /// <summary>Sirus seated on the throne: the arena's first stage, which casts without walking off the platform.</summary>
    internal static bool IsSirusSeated(MonsterCatalog.Variety? variety) => variety?.Id is SirusThrone;
    // The Maven (MavenBoss "Absence of Mercy and Empathy", level 84) and the Nucleus her death forms in the same
    // arena: the two halves of one encounter, both scripted (MavenFight.cs).
    internal static bool IsMaven(MonsterCatalog.Variety? variety) => variety?.Id is MavenNucleus.Maven;
    internal static bool IsNucleus(MonsterCatalog.Variety? variety) => variety?.Id is MavenNucleus.Nucleus;
    internal static bool IsBoss(MonsterCatalog.Variety? variety) =>
        IsShaper(variety) || IsElder(variety) || IsSirus(variety) || IsMaven(variety) || IsNucleus(variety);

    // The effect whose row the monster's own record uses as its swing: the melee, or Sirus' beam blast (he never swings a
    // weapon). Everything else is played by the controller through ids of its own, so the ordinary swing can only ever be
    // that one skill. The Maven's record swings her lightning shot (the plain AI played the same row) and the Nucleus'
    // its small barrage.
    internal static int BasicEffect(MonsterCatalog.Variety variety) =>
        IsNucleus(variety) ? NucleusSmallProjectileBarrageEffect
        : IsMaven(variety) ? MavenProjectileEffect
        : IsSirus(variety) ? SirusBeamBlastEffect : IsElder(variety) ? ElderMeleeEffect : ShaperMeleeEffect;

    // A melee keeps the variety's own attack time and reach; Sirus', The Maven's and the Nucleus' basic skills are
    // spells and use their clips' (none of the three ever swings a weapon).
    internal static bool MeleeLike(MonsterCatalog.Variety variety) => IsShaper(variety) || IsElder(variety);

    // 0x4000-0x4005 the Shaper, 0x4010-0x4015 the Elder, 0x4020-0x402c Sirus, 0x4030-0x4038 The Maven and
    // 0x4040-0x4049 her Nucleus: one id per effect, fixed, so an id always means one clip. The Maven and the Nucleus
    // never stand in one arena at the same time (she forms it by dying), the disjoint blocks are kept anyway.
    internal static readonly int[] ShaperEffects = [ShaperMeleeEffect, CelestialBeamEffect, AcceleratingProjectilesEffect,
        FlickerSlamEffect, RetreatEffect, BulletHellEffect];
    internal static readonly int[] ElderEffects = [ElderMeleeEffect, SiphonEffect, SlammingTentaclesEffect,
        ExpandingNovaEffect, DoomProjectileEffect, ProjectileSpellcastEffect];
    internal static readonly int[] SirusEffects = [SirusBeamBlastEffect, SirusCorridorBlastEffect, SirusCircleMazeEffect,
        SirusCircleMaze2Effect, SirusCircleMaze3Effect, SirusCircleMaze4Effect, SirusCircleMaze5Effect, SirusMultiBeamEffect,
        SirusMultiBeam2Effect, SirusCloneBeamEffect, SirusMeteorEffect, SirusMeteorGroundEffect, SirusBeamBombEffect];
    // The Maven: her lightning shot, the circle slam (the EAS row is the clip, MavenCircleSlam the hit), the cold
    // cannon (EASCannon's clip, MavenCannonColdProjectile the shot), the super fire projectile and the impact it lands,
    // the laser barrage (EASLaserBarrage's clip) and the beam each of its six targets fires.
    internal static readonly int[] MavenEffects = [MavenProjectileEffect, MavenEASCircleSlamEffect, MavenCircleSlamEffect,
        MavenEASCannonEffect, MavenCannonColdProjectileEffect, MavenSuperFireProjectileEffect,
        MavenSuperFireProjectileImpactEffect, MavenLaserBarrageEffect, MavenLaserBeamEffect];
    // The Nucleus: its record's own small barrage, the charge beam, the ion cannon, the beam blast, the three
    // projectile barrages (their MPS rows carry the shots), the large barrage's impact, the decelerating shot and the
    // barrage's explosion.
    internal static readonly int[] NucleusEffects = [NucleusSmallProjectileBarrageEffect, NucleusChargeBeamEffect,
        NucleusIonCannonEffect, NucleusBeamBlastEffect, NucleusProjectileBlastMpsEffect, NucleusLargeProjectileBarrageMpsEffect,
        NucleusSmallProjectileBarrageMpsEffect, NucleusLargeProjectileImpactEffect, NucleusDeceleratingProjectileEffect,
        NucleusProjectileBarrageExplodeEffect];

    internal static int[] EffectsFor(MonsterCatalog.Variety variety) =>
        IsMaven(variety) ? MavenEffects : IsNucleus(variety) ? NucleusEffects
        : IsSirus(variety) ? SirusEffects : IsElder(variety) ? ElderEffects : ShaperEffects;

    /// <summary>The skill id the client's actor uses for one of the boss's effects.</summary>
    internal static ushort Skill(MonsterCatalog.Variety variety, int effect)
    {
        var effects = EffectsFor(variety);
        int index = Array.IndexOf(effects, effect);
        int first = IsMaven(variety) ? 0x4030 : IsNucleus(variety) ? 0x4040
            : IsSirus(variety) ? 0x4020 : IsElder(variety) ? 0x4010 : 0x4000;
        // An effect outside the kit keeps an id of its own rather than colliding with the basic skill's.
        return (ushort)(first + (index >= 0 ? index : effects.Length + 1));
    }

    /// <summary>The actor's skill list: every effect the controller can play, so that the client owns the clip a 0x1ab
    /// action names. Each record carries its own numbers (main-hand damage, the spell's virtual cast time), which the
    /// client reads to pick and play the clip; <see cref="Attack"/> supplies the same numbers to the server's hits.</summary>
    internal static IReadOnlyList<GrantedSkill>? SkillsFor(ClientState conn, MonsterCatalog.Variety variety, int level,
        MonsterAttack attack)
    {
        if (!IsBoss(variety)) return null;
        var catalog = Game.SkillsFor(conn);
        var effects = EffectsFor(variety);
        return [.. effects.Select(effect =>
        {
            var use = Attack(conn, variety, level, effect, attack);
            return catalog.FindByRow(Skill(variety, effect), effect, level,
                WithBehaviourVariation(conn, effect, use.SkillStats()));
        }).OfType<GrantedSkill>()];
    }

    // Sirus' effects carry their spell_minimum/maximum_base_physical_damage as a fraction of the monster's own damage,
    // not as a flat hit (0.8-1.2 the beam blast, 1.6-2.4 the corridor "Die" beam, 1.2-1.8 the circle-maze blasts,
    // 1.6-2.4 the clone and multi beams, 0.8-1.2 the beam bomb; the meteor arena's row names no damage at all and its
    // meteors land like the corridor's). EffectStats rounds those fractions to whole numbers, so the row cannot be read
    // as the hit and the fraction is applied here to BaseDamage instead.
    static readonly Dictionary<int, (double Min, double Max)> SirusFractions = new()
    {
        [SirusBeamBlastEffect] = (0.8, 1.2),
        [SirusCorridorBlastEffect] = (1.6, 2.4),
        [SirusCircleMazeEffect] = (1.2, 1.8),
        [SirusCircleMaze2Effect] = (1.2, 1.8),
        [SirusCircleMaze3Effect] = (1.2, 1.8),
        [SirusCircleMaze4Effect] = (1.2, 1.8),
        [SirusCircleMaze5Effect] = (1.2, 1.8),
        [SirusCloneBeamEffect] = (1.6, 2.4),
        [SirusMultiBeamEffect] = (1.6, 2.4),
        [SirusMultiBeam2Effect] = (1.6, 2.4),
        [SirusMeteorEffect] = (1.6, 2.4),
        [SirusMeteorGroundEffect] = (0.4, 0.6),
        [SirusBeamBombEffect] = (0.8, 1.2),
    };

    /// <summary>One of Sirus' hits, as the fraction of the variety's physical damage its effect row carries.</summary>
    internal static DamagePacket SirusDamage(int effect, int min, int max)
    {
        var (low, high) = SirusFractions.GetValueOrDefault(effect, (1.0, 1.0));
        return DamagePacket.Of(DamageType.Physical,
            new DamageRange((int)Math.Floor(min * low), (int)Math.Ceiling(max * high)));
    }

    // The Maven's and the Nucleus' effects carry their damage the way Sirus' do (the last per-level row's
    // FloatStatsValues as a fraction of the variety's own damage), but each row names its own element instead of
    // physical: The Maven's shot is lightning 0.75-2.25, her circle slam physical 1.6-2.4, the cold cannon 0.8-1.2,
    // the super fire projectile's impact 1.6-2.4 and a laser beam 1.6-2.4; the Nucleus' charge beam and beam blast
    // physical 0.8-1.2, its ion cannon 0.4-0.6 of lightning *and* cold, the projectile blast 0.8-1.2, the large
    // barrage and its impact 0.5-1.5, the small barrage 0.8-1.2, the decelerating shot 0.8-1.2 and the barrage's
    // explosion 0.8-1.2. The super fire projectile's own row resolves to a single point of damage at every level: the
    // shot is a launcher and the impact row carries the hit, so it is not in the table.
    static readonly Dictionary<int, (DamageType Type, double Min, double Max)[]> MavenFractions = new()
    {
        [MavenProjectileEffect] = [(DamageType.Lightning, 0.75, 2.25)],
        [MavenCircleSlamEffect] = [(DamageType.Physical, 1.6, 2.4)],
        [MavenCannonColdProjectileEffect] = [(DamageType.Cold, 0.8, 1.2)],
        [MavenSuperFireProjectileImpactEffect] = [(DamageType.Fire, 1.6, 2.4)],
        [MavenLaserBeamEffect] = [(DamageType.Fire, 1.6, 2.4)],
        [NucleusSmallProjectileBarrageEffect] = [(DamageType.Cold, 0.8, 1.2)],
        [NucleusChargeBeamEffect] = [(DamageType.Physical, 0.8, 1.2)],
        [NucleusIonCannonEffect] = [(DamageType.Lightning, 0.4, 0.6), (DamageType.Cold, 0.4, 0.6)],
        [NucleusBeamBlastEffect] = [(DamageType.Physical, 0.8, 1.2)],
        [NucleusProjectileBlastMpsEffect] = [(DamageType.Lightning, 0.8, 1.2)],
        [NucleusLargeProjectileBarrageMpsEffect] = [(DamageType.Lightning, 0.5, 1.5)],
        [NucleusSmallProjectileBarrageMpsEffect] = [(DamageType.Lightning, 0.8, 1.2)],
        [NucleusLargeProjectileImpactEffect] = [(DamageType.Lightning, 0.5, 1.5)],
        [NucleusDeceleratingProjectileEffect] = [(DamageType.Cold, 0.8, 1.2)],
        [NucleusProjectileBarrageExplodeEffect] = [(DamageType.Cold, 0.8, 1.2)],
    };

    // Three rows convert all of their physical damage to fire, cold and lightning (the stat set names
    // skill_physical_damage_%_to_convert_to_fire/cold/lightning = 100 each). The game caps a type's outgoing
    // conversion at 100%, so each destination keeps a third: the Maven's slam and the Nucleus' charge beam and beam
    // blast hit with fire, cold and lightning alike.
    static readonly HashSet<int> MavenConverted = [MavenCircleSlamEffect, NucleusChargeBeamEffect, NucleusBeamBlastEffect];

    /// <summary>One of The Maven's or the Nucleus' hits, as the fraction of the variety's own damage its effect row
    /// carries at the area's level, with the row's own element and its conversion when it names one. An effect this
    /// table does not know (an action-script clip) deals no hit at all.</summary>
    internal static DamagePacket MavenDamage(int effect, int min, int max)
    {
        if (!MavenFractions.TryGetValue(effect, out var parts)) return DamagePacket.Empty;
        var packet = DamagePacket.Empty;
        foreach (var (type, low, high) in parts)
            packet = packet.Add(type, new DamageRange(Math.Floor(min * low), Math.Ceiling(max * high)));
        if (!MavenConverted.Contains(effect)) return packet;
        var conversion = new DamageConversion()
            .AddSkill(DamageType.Physical, DamageType.Lightning, 100)
            .AddSkill(DamageType.Physical, DamageType.Cold, 100)
            .AddSkill(DamageType.Physical, DamageType.Fire, 100);
        var converted = DamagePacket.Empty;
        foreach (var (type, range) in conversion.Apply(packet).Components)
            converted = converted.Add(type, new DamageRange(Math.Floor(range.Min), Math.Ceiling(range.Max)));
        return converted;
    }

    /// <summary>One of the boss's skills as the server plays it: its own damage at the area's level, its clip's cast time
    /// and reach, and — for the skills that name one — its projectile. <paramref name="swing"/> is the variety's plain
    /// attack, the source of a melee's physical damage.</summary>
    internal static MonsterAttack Attack(ClientState conn, MonsterCatalog.Variety variety, int level, int effect,
        MonsterAttack? swing = null)
    {
        var catalog = Game.SkillsFor(conn);
        var (min, max) = Monsters.BaseDamage(variety, (byte)Math.Clamp(level, 1, 100)) ?? (1, 1);
        bool melee = effect == BasicEffect(variety) && MeleeLike(variety);
        // The skill's own base hit: its spell_minimum_base_*_damage (HitDamage also reads the unprefixed names, so an
        // attack such as a slam keeps its numbers too). A slam or a melee whose stat set carries no base damage of its own
        // hits for the variety's physical damage, the way every monster's swing does. The two degen skills are the
        // exception: their whole damage is the per-minute degen DamageOverTime reads, and a physical fallback there would
        // put an invisible hit on top of the beam or the siphon.
        var damage = catalog.HitDamage(effect, level);
        if (IsSirus(variety)) damage = SirusDamage(effect, min, max);
        else if (IsMaven(variety) || IsNucleus(variety)) damage = MavenDamage(effect, min, max);
        else if (damage.MaxTotal <= 0 && effect is not (CelestialBeamEffect or SiphonEffect))
            damage = DamagePacket.Of(DamageType.Physical, min, max);
        double seconds = melee ? Math.Max(0.3, variety.AttackMs / 1000.0) : Math.Max(0.3, catalog.CastTimeMs(effect) / 1000.0);
        double reach = melee ? variety.AttackRange : 45;
        if (melee && swing is not null) return swing with { SkillId = Skill(variety, effect), Scripted = true };
        var projectile = ProjectileFor(conn, effect);
        return new MonsterAttack(Skill(variety, effect), seconds, reach, damage)
        {
            Spell = catalog.IsSpell(effect),
            Scripted = true,
            Ranged = projectile is not null,
            Projectile = projectile,
        };
    }

    /// <summary>The shot a skill launches: its own MonsterProjectileVariation row when the skill has one, and the
    /// Projectiles.dat row for the two that name none (the bullet-hell ring is twelve in a circle, which no skill record
    /// makes, and the doom shot is a single slow one).</summary>
    internal static SkillCatalog.MonsterProjectileInfo? ProjectileFor(ClientState conn, int effect) => effect switch
    {
        BulletHellEffect => BulletHellShot,
        DoomProjectileEffect => DoomShot,
        // The Maven's and the Nucleus' shots are monster_projectile_spell rows: they name no mortar, and their
        // monster_projectile_variation (176-178, 173-175) is not a Projectiles.dat row number. Each one's art, speed
        // and collision radius are the Projectiles.dat row of its own name, as that table lists them.
        MavenProjectileEffect => MavenShot("MavenProjectile", 2250, 3),
        MavenCannonColdProjectileEffect => MavenShot("MavenCannonColdProjectile", 1500, 4),
        MavenSuperFireProjectileEffect => MavenShot("MavenSuperFireProjectile", 750, 4),
        NucleusSmallProjectileBarrageEffect => MavenShot("MavenBrainSmallProjectileBarrage", 750, 3),
        NucleusProjectileBlastMpsEffect => MavenShot("MavenBrainProjectileBlast", 750, 2),
        NucleusLargeProjectileBarrageMpsEffect => MavenShot("MavenBrainLargeProjectileBarrage", 300, 3),
        NucleusSmallProjectileBarrageMpsEffect => MavenShot("MavenBrainSmallProjectileBarrage", 750, 3),
        NucleusDeceleratingProjectileEffect => MavenShot("MavenBrainDeceleratingProjectile", 750, 3),
        _ => Game.SkillsFor(conn).MonsterProjectile(effect),
    };

    /// <summary>One of The Maven's or the Nucleus' shots: the Projectiles.dat row of that name with its own speed and
    /// collision radius, played through the monster projectile spell's cast animation.</summary>
    static SkillCatalog.MonsterProjectileInfo MavenShot(string id, int speed, int radius) =>
        new($"Metadata/Projectiles/{id}", speed, false, radius) { AnimationKey = "SpellProjectile" };

    /// <summary>A degen skill's damage from its own stat set: the base fire/cold/lightning/physical/chaos
    /// <c>..._damage_to_deal_per_minute</c> over one second. The Shaper's celestial beam carries fire 5384, cold 4415 and
    /// lightning 6084 a second (323067, 264915 and 365066 a minute), the Elder's siphon 614 physical (36825 a minute);
    /// the rows are constant at every level, not per-level.
    /// </summary>
    internal static DamagePacket DamageOverTime(ClientState conn, int effect, int level)
    {
        var stats = Game.SkillsFor(conn).EffectStats(effect, level);
        int Get(string name)
        {
            if (Stats.Key(name) is not uint key) return 0;
            foreach (var (id, value) in stats) if (id == key) return value;
            return 0;
        }
        var packet = DamagePacket.Empty;
        foreach (var (type, name) in new[]
        {
            (DamageType.Physical, "base_physical_damage_to_deal_per_minute"),
            (DamageType.Fire, "base_fire_damage_to_deal_per_minute"),
            (DamageType.Cold, "base_cold_damage_to_deal_per_minute"),
            (DamageType.Lightning, "base_lightning_damage_to_deal_per_minute"),
            (DamageType.Chaos, "base_chaos_damage_to_deal_per_minute"),
        })
        {
            double perSecond = Get(name) / 60.0;
            if (perSecond < 1) continue;
            packet = packet.Add(type, new DamageRange(Math.Floor(perSecond), Math.Ceiling(perSecond)));
        }
        return packet;
    }

    /// <summary>Runs the fight of every Shaper, Elder and Sirus in this connection's instance. Called once per tick per
    /// connection, like <see cref="BrineKing.Tick"/>: the instance's runner plays the steps, a join only binds.</summary>
    internal static async Task Tick(ClientState conn)
    {
        if (!conn.PlayerSent || conn.Closed || conn.LeavingArea) return;
        List<(uint Id, SpawnedMonster Monster)> found = [];
        lock (conn.Monsters)
            foreach (var (id, monster) in conn.Monsters)
                if (IsBoss(monster.Variety)) found.Add((id, monster));
        if (found.Count == 0 && !Fights.TryGetValue(conn.Instance, out _)) return;
        foreach (var (id, monster) in found)
        {
            if (!Fights.TryGetValue(conn.Instance, out var byMonster))
                Fights.Add(conn.Instance, byMonster = new Dictionary<uint, AtlasBossFight>());
            if (!byMonster.TryGetValue(id, out var fight))
            {
                var variety = monster.Variety!;
                fight = IsMaven(variety) ? new MavenFight(id, monster, conn)
                    : IsNucleus(variety) ? new NucleusFight(id, monster, conn)
                    : IsSirus(variety) ? new SirusFight(id, monster, conn)
                    : IsShaper(variety) ? new ShaperFight(id, monster, conn)
                    : new ElderFight(id, monster, conn);
                byMonster[id] = fight;
                Console.WriteLine($"[gateway] {conn.Wire.Remote} 0x{id:x} {variety.Name ?? variety.Id} "
                    + $"({variety.Id}, level {monster.Level}, life {monster.MaxLife}): fight started, "
                    + $"{(IsMonsterRunner(conn) ? "its steps run here" : "another connection runs its steps")}");
            }
            await fight.Gate.WaitAsync();
            try
            {
                await fight.Bind(conn);
                if (IsMonsterRunner(conn)) await fight.Step(conn, conn.Clock.Elapsed.TotalSeconds);
            }
            catch (Exception ex)
            {
                // Guard logs an exception that escapes a tick and drops the hook; a fight that cannot step must say so.
                Console.WriteLine($"[gateway] {conn.Wire.Remote} {monster.Variety!.Id} 0x{id:x} step failed: {ServerLog.Describe(ex)}");
            }
            finally { fight.Gate.Release(); }
        }
    }

    static readonly ConditionalWeakTable<InstanceState, Dictionary<uint, AtlasBossFight>> Fights = new();

    /// <summary>Stops every fight of an instance (it is unloaded): the phase gates unlock and the boss's own walk speed
    /// returns.</summary>
    internal static IDisposable CleanupOnUnload() => new Cleanup();

    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights)
                foreach (var fight in pair.Value.Values) fight.Stop();
        }
    }
}
