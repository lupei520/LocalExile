namespace GameServer.Gateway.Tests;

// The corpse of a dead monster blocks the client's paths until its death clip clears the body. The time is the
// variety's own (data/monster-takehit.json "death.unblock"), which the server reads from client.db: a server that
// keeps its game data in the database has no monster-takehit.json on disk, and every corpse would then block for
// 150 ms whatever the variety's clip says.
public sealed class MonsterCorpseTests
{
    [Fact]
    public void The_body_clear_time_is_the_variety_death_clips_own()
    {
        var catalog = new MonsterCatalog();
        Assert.Equal(166, catalog.CorpseUnblockMs("Metadata/Monsters/AtlasBosses/ChimeraBoss"));
        Assert.Equal(1833, catalog.CorpseUnblockMs("Metadata/Monsters/ZombieBoss/MapZombieBossHillock"));
        // A death clip with no unblock clears the body with the death itself.
        Assert.Null(catalog.CorpseUnblockMs("Metadata/Monsters/AbyssTwoLeague/AbyssPinnacleHalfSkeleton"));
    }
}
