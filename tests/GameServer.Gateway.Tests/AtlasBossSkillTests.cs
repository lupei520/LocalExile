using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// The Atlas pinnacle bosses played one granted effect as a plain swing, because nothing took their kit: their varieties
// list it in GrantedEffectsKeys and the client can only play an action whose skill id its actor owns (AtlasBosses.cs).
public class AtlasBossSkillTests
{
    // The four rows that fight with Sirus' kit; the standalone arenas grant everything but the two teleports.
    static readonly string[] SirusVarieties =
    [
        AtlasBosses.Sirus, AtlasBosses.SirusGauntlet, AtlasBosses.SirusStandalone, AtlasBosses.SirusStandaloneMirage,
    ];

    [Fact]
    public void The_bosses_own_their_kits_their_hits_and_their_own_skill_ids()
    {
        var skills = new SkillCatalog();
        var monsters = new MonsterCatalog();
        var shaper = monsters.Find(AtlasBosses.Shaper);
        var elder = monsters.Find(AtlasBosses.Elder);
        Assert.NotNull(shaper);
        Assert.NotNull(elder);
        Assert.True(AtlasBosses.IsShaper(shaper));
        Assert.True(AtlasBosses.IsElder(elder));
        Assert.False(AtlasBosses.IsBoss(monsters.Find("Metadata/Monsters/Zombie/Zombie")));

        Assert.Equal("ShaperMelee", skills.EffectId(AtlasBosses.BasicEffect(shaper!)));
        Assert.Equal("Melee", skills.EffectId(AtlasBosses.BasicEffect(elder!)));
        // Each id in the kit is one of the boss's own granted effects and carries a clip the actor can play.
        foreach (int effect in AtlasBosses.ShaperEffects) Assert.Contains(effect, shaper!.GrantedEffects!);
        foreach (int effect in AtlasBosses.ElderEffects) Assert.Contains(effect, elder!.GrantedEffects!);
        Assert.Equal("AtlasBossCelestialBeam", skills.EffectId(6093));
        Assert.Equal("ElderSiphon", skills.EffectId(9058));

        // Sirus never swings a weapon: his record's own skill is the beam blast, and every id of his kit is granted too.
        foreach (string id in SirusVarieties)
        {
            var variant = monsters.Find(id);
            Assert.NotNull(variant);
            Assert.True(AtlasBosses.IsSirus(variant));
            Assert.True(AtlasBosses.IsBoss(variant));
            Assert.False(AtlasBosses.MeleeLike(variant!));
            Assert.Equal(AtlasBosses.SirusBeamBlastEffect, AtlasBosses.BasicEffect(variant!));
            foreach (int effect in AtlasBosses.SirusEffects) Assert.Contains(effect, variant!.GrantedEffects!);
        }
        // AtlasExilesBoss5 is a phased arena (MapBossFights): WorldAreas lists the throne row first and the walking row as
        // the held-back last boss, so a freshly opened arena contains only the throne — it has to be Sirus too, or the
        // player walks into an arena whose only boss does nothing.
        var throne = monsters.Find(AtlasBosses.SirusThrone);
        Assert.NotNull(throne);
        Assert.True(AtlasBosses.IsSirus(throne));
        Assert.True(AtlasBosses.IsSirusSeated(throne));
        Assert.False(AtlasBosses.IsSirusSeated(monsters.Find(AtlasBosses.Sirus)));
        // The throne row grants no effects of its own: the kit it plays is the controller's, which is why it needs the
        // actor list from SirusEffects and not from its own row.
        Assert.Empty(throne!.GrantedEffects!);

        // Sirus' effect rows carry their base physical damage as a fraction of his own, not as a flat hit: the corridor
        // beam is 1.6-2.4 of it, so a 10 base becomes 16-24 (the fractions are why the row cannot be read as the hit).
        var corridor = AtlasBosses.SirusDamage(AtlasBosses.SirusCorridorBlastEffect, 10, 10);
        Assert.Equal(16, corridor.MinTotal);
        Assert.Equal(24, corridor.MaxTotal);

        // The Maven and the Nucleus her death forms are the same kind of fight. Her kit is her own granted effects plus
        // the beam impact her six invisible laser targets fire (1872): the server spawns no actor for those six, so she
        // plays their beams herself. The Nucleus' kit is entirely its own.
        var maven = monsters.Find(Game.MavenNucleus.Maven);
        var nucleus = monsters.Find(Game.MavenNucleus.Nucleus);
        Assert.NotNull(maven);
        Assert.NotNull(nucleus);
        Assert.True(AtlasBosses.IsMaven(maven));
        Assert.True(AtlasBosses.IsNucleus(nucleus));
        Assert.True(AtlasBosses.IsBoss(maven));
        Assert.True(AtlasBosses.IsBoss(nucleus));
        Assert.False(AtlasBosses.MeleeLike(maven!));
        Assert.False(AtlasBosses.MeleeLike(nucleus!));
        Assert.Equal(AtlasBosses.MavenProjectileEffect, AtlasBosses.BasicEffect(maven!));
        Assert.Equal(AtlasBosses.NucleusSmallProjectileBarrageEffect, AtlasBosses.BasicEffect(nucleus!));
        foreach (int effect in AtlasBosses.MavenEffects)
            if (effect != AtlasBosses.MavenLaserBeamEffect) Assert.Contains(effect, maven!.GrantedEffects!);
        foreach (int effect in AtlasBosses.NucleusEffects) Assert.Contains(effect, nucleus!.GrantedEffects!);

        // Their rows carry the hit the same way, with the row's own element: her shot is lightning 0.75-2.25 of the
        // variety's damage and her slam physical 1.6-2.4 converted in thirds (three 100% conversions are capped at 100%
        // by the game's conversion table, so fire, cold and lightning keep a third each and 1% stays physical).
        var shot = AtlasBosses.MavenDamage(AtlasBosses.MavenProjectileEffect, 100, 100);
        Assert.Equal(75, shot[DamageType.Lightning].Min);
        Assert.Equal(225, shot[DamageType.Lightning].Max);
        Assert.True(shot[DamageType.Physical].IsZero);
        var slam = AtlasBosses.MavenDamage(AtlasBosses.MavenCircleSlamEffect, 100, 100);
        Assert.Equal(52, slam[DamageType.Fire].Min);
        Assert.Equal(80, slam[DamageType.Fire].Max);
        Assert.Equal(52, slam[DamageType.Cold].Min);
        Assert.Equal(52, slam[DamageType.Lightning].Min);
        // The Nucleus' ion cannon is lightning and cold at once, and the super fire projectile's own row (a launcher
        // that resolves to a single point of damage) lands nothing while the impact it leaves does.
        var ion = AtlasBosses.MavenDamage(AtlasBosses.NucleusIonCannonEffect, 100, 100);
        Assert.Equal(40, ion[DamageType.Lightning].Min);
        Assert.Equal(60, ion[DamageType.Cold].Max);
        Assert.True(AtlasBosses.MavenDamage(AtlasBosses.MavenSuperFireProjectileEffect, 100, 100).IsZero);
        Assert.Equal(240, AtlasBosses.MavenDamage(AtlasBosses.MavenSuperFireProjectileImpactEffect, 100, 100)
            [DamageType.Fire].Max);
        // An action-script clip carries no damage of its own: playing it can only ever be the animation.
        Assert.True(AtlasBosses.MavenDamage(AtlasBosses.MavenEASCircleSlamEffect, 100, 100).IsZero);

        // The ids: one per effect, fixed, and never a gem id (a gem id on a monster action makes the client start
        // that gem's action instead of the boss's clip: 0xc0000005, live 2026-10-31).
        var sirus = monsters.Find(AtlasBosses.Sirus)!;
        var ids = new List<ushort>();
        foreach (int effect in AtlasBosses.ShaperEffects) ids.Add(AtlasBosses.Skill(shaper!, effect));
        foreach (int effect in AtlasBosses.ElderEffects) ids.Add(AtlasBosses.Skill(elder!, effect));
        foreach (int effect in AtlasBosses.SirusEffects) ids.Add(AtlasBosses.Skill(sirus, effect));
        foreach (int effect in AtlasBosses.MavenEffects) ids.Add(AtlasBosses.Skill(maven!, effect));
        foreach (int effect in AtlasBosses.NucleusEffects) ids.Add(AtlasBosses.Skill(nucleus!, effect));

        foreach (ushort id in ids)
        {
            Assert.True(id >= 0x4000, $"skill id 0x{id:x4} is in the player's range");
            Assert.False(id is >= 0x0103 and <= 0x0142, $"skill id 0x{id:x4} is a gem id");
        }
        Assert.Equal(ids.Count, ids.Distinct().Count());
        // The families are what keeps them apart: the Shaper and the Elder stand in one arena together, and The Maven
        // and her Nucleus are one encounter.
        Assert.All(AtlasBosses.SirusEffects, e => Assert.InRange(AtlasBosses.Skill(sirus, e), 0x4020, 0x402f));
        Assert.All(AtlasBosses.MavenEffects, e => Assert.InRange(AtlasBosses.Skill(maven!, e), 0x4030, 0x403f));
        Assert.All(AtlasBosses.NucleusEffects, e => Assert.InRange(AtlasBosses.Skill(nucleus!, e), 0x4040, 0x404f));
    }
}
