namespace GameServer.Gateway.Tests;

public sealed class SplittingSteelCatalogTests
{
    // 3.29 Projectiles.dat has no Metadata/Projectiles/SplittingSteel row; the skill's only steel-family row is
    // ImpactingSteelProjectile (the name its art still carries: ImpactingSteelSkill.dds / ImpactingSteelGem.dds).
    [Fact]
    public void SplittingSteelResolvesItsProjectileDefinition()
    {
        var skills = new SkillCatalog();
        int splittingSteel = skills.EffectRow("SplittingSteel");
        Assert.True(splittingSteel >= 0);
        var projectile = skills.ProjectileFor(splittingSteel);
        Assert.NotNull(projectile);
        Assert.Equal("Metadata/Projectiles/ImpactingSteelProjectile", projectile!.Value.Id);
        Assert.Equal(5000, projectile.Value.Speed);
    }

    // Its two transfigured versions grant their own effects and reach the same row through the base effect.
    [Theory]
    [InlineData("SplittingSteelAltX")]
    [InlineData("SplittingSteelAltY")]
    public void SplittingSteelTransfiguredResolvesItsProjectileDefinition(string effect)
    {
        var skills = new SkillCatalog();
        int row = skills.EffectRow(effect);
        Assert.True(row >= 0);
        var projectile = skills.ProjectileFor(row);
        Assert.NotNull(projectile);
        Assert.Equal("Metadata/Projectiles/ImpactingSteelProjectile", projectile!.Value.Id);
    }
}
