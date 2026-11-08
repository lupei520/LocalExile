using GameServer.Modules.M_questobjects;
using static Act2Areas;
using static Quests;

namespace GameServer.Gateway.Tests;

/// <summary>
/// a2q6 "Intruders in Black" (issue #578): the Strange Device opens once Fidelitas dies, so the Baleful Gem can be taken
/// before Helena's "Rescued" as well as after it. The client's own rows say so: NPCTalk 456 needs HaveSkillGem and not
/// RescuedHelena, 457 the other way round (12-quests.md §11, act2.md). See <see cref="QuestSimulator"/>.
/// </summary>
public sealed class StrangeDeviceUnlockTests
{
    // The Chamber of Sins Level 2's WorldAreas gate is a1q3-KilledSeaWitch; travel skips the road's exits.
    static QuestSimulator InChamber()
    {
        var s = new QuestSimulator();
        s.Set(["a1q3-KilledSeaWitch"]).Enter(ChamberOfSins2, travel: true);
        return s;
    }

    [Fact]
    public void Device_is_shut_before_the_kill_and_opens_with_it()
    {
        using var registered = Register("StrangeDeviceUnlockTests", QuestObjectsModule.Acts());

        var before = InChamber();
        Assert.Throws<QuestSimulator.StepFailed>(() => before.OpenChest(Act2Objects.StrangeDevice));

        var after = InChamber();
        after.Kill(Act2Monsters.Fidelitas);
        after.OpenChest(Act2Objects.StrangeDevice);
        after.PickUp(Act2Items.BalefulGem);
        Assert.True(after.Has("a2q6-HaveSkillGem"));
        Assert.False(after.Has("a2q6-RescuedHelena"));
    }

    [Fact]
    public void Gem_first_or_Helena_first_both_leave_the_other_step_reachable()
    {
        using var registered = Register("StrangeDeviceUnlockTests", QuestObjectsModule.Acts());

        // Gem first: the device drops it, and Helena is still there to rescue (row 456, the gem carried).
        var gemFirst = InChamber();
        gemFirst.Kill(Act2Monsters.Fidelitas).OpenChest(Act2Objects.StrangeDevice).PickUp(Act2Items.BalefulGem);
        Assert.True(gemFirst.Has("a2q6-HaveSkillGem"));
        gemFirst.Talk(Act2Objects.HelenaSins, "Rescued");
        Assert.True(gemFirst.Has("a2q6-RescuedHelena"));
        Assert.Contains(gemFirst.Log, l => l.Contains("row 456"));

        // Helena first: her rescue leaves the device open (the kill did it), so the gem follows (row 457).
        var helenaFirst = InChamber();
        helenaFirst.Kill(Act2Monsters.Fidelitas).Talk(Act2Objects.HelenaSins, "Rescued");
        Assert.True(helenaFirst.Has("a2q6-RescuedHelena"));
        Assert.False(helenaFirst.Has("a2q6-HaveSkillGem"));
        Assert.Contains(helenaFirst.Log, l => l.Contains("row 457"));
        helenaFirst.OpenChest(Act2Objects.StrangeDevice).PickUp(Act2Items.BalefulGem);
        Assert.True(helenaFirst.Has("a2q6-HaveSkillGem"));
    }
}