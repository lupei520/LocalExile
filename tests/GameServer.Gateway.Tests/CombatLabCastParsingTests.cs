namespace GameServer.Gateway.Tests;

/// <summary>
/// /cast's argument parsing (Modules/Core/Player/Commands/Rules.CombatLab.cs). Live 2026-11-09: "/cast Splitting Steel
/// Rhoa" was answered with the usage error because the gem's name has a space, so the lab could not name most of the
/// skill list. The split is greedy on the leading run — the longest run of arguments that names exactly one gem is the
/// skill — and it never picks a shorter run silently.
/// </summary>
public sealed class CombatLabCastParsingTests
{
    // SkillMatchByName's shape: exactly one candidate is a gem this character has, several is ambiguous, none is unknown.
    static Func<string, Game.SkillMatch> Finds(params string[] names) => text =>
        names.Contains(text, StringComparer.OrdinalIgnoreCase)
            ? new Game.SkillMatch(0x0105, 1, text, 20, 0, [])
            : new Game.SkillMatch(0, -1, "", 0, 0, []);

    static Func<string, Game.SkillMatch> Ambiguous(string name, params string[] candidates) => text =>
        string.Equals(text, name, StringComparison.OrdinalIgnoreCase)
            ? new Game.SkillMatch(0, -1, "", 0, 0, candidates)
            : new Game.SkillMatch(0, -1, "", 0, 0, []);

    [Fact]
    public void Cast_takes_the_longest_leading_run_as_the_gem_name()
    {
        var match = Finds("Splitting Steel");
        // "/cast Splitting Steel Rhoa" -> skill "Splitting Steel", aim "Rhoa".
        var (skill, aim) = CombatLab.SplitCast(["Splitting", "Steel", "Rhoa"], match);
        Assert.Equal("Splitting Steel", skill);
        Assert.Equal(new[] { "Rhoa" }, aim);

        // A quoted first token is one token, and behaves identically.
        var (quoted, quotedAim) = CombatLab.SplitCast(["\"Splitting Steel\"", "Rhoa"], match);
        Assert.Equal("Splitting Steel", quoted);
        Assert.Equal(new[] { "Rhoa" }, quotedAim);

        // The single-token case is unchanged, and the splitter leaves the aim alone (an x y pair stays two arguments).
        var (one, none) = CombatLab.SplitCast(["Splitting Steel"], match);
        Assert.Equal("Splitting Steel", one);
        Assert.Empty(none);
        var (cell, xy) = CombatLab.SplitCast(["Splitting", "Steel", "120", "340"], match);
        Assert.Equal("Splitting Steel", cell);
        Assert.Equal(new[] { "120", "340" }, xy);
    }

    [Fact]
    public void Cast_refuses_rather_than_picking_a_shorter_run()
    {
        // Only "Steel" names a gem, and it is not a leading run: nothing may be picked silently and no aim consumed.
        var (skill, aim) = CombatLab.SplitCast(["Splitting", "Steel", "Rhoa"], Finds("Steel"));
        Assert.Equal("Splitting", skill);
        Assert.Empty(aim);

        // A run that names several gems is what the refusal speaks about, so the caller can list them.
        var (ambiguous, ambiguousAim) = CombatLab.SplitCast(["Ice", "Nova", "Rhoa"],
            Ambiguous("Ice Nova", "0x0001 Ice Nova", "0x0002 Ice Nova of Frostbolts"));
        Assert.Equal("Ice Nova", ambiguous);
        Assert.Empty(ambiguousAim);

        // An unknown name falls back to the first argument, which is what the old refusal spoke about.
        var (unknown, _) = CombatLab.SplitCast(["Nope", "Nope", "Rhoa"], Finds("Splitting Steel"));
        Assert.Equal("Nope", unknown);
    }
}
