using Roshiq.Core.Models;
using Roshiq.Data;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>Итоги линии по парам героев (lane_matchups.txt, STRATZ) в советах на линию.</summary>
public class LaneMatchupTests
{
    private static Hero H(int id, string key, params string[] traits) =>
        new(id, key, key, key, PrimaryAttribute.Strength, new[] { Position.Offlane }, traits);

    [Fact]
    public void LaneLine_ToughLane_WarnsAndCountsStomps()
    {
        var pudge = H(14, "pudge");
        var advice = LaneAdvisor.Advise(new[] { pudge }, Array.Empty<LaneTip>(),
            lane: _ => new LaneMatchup(1000, 380, 150, 470, 90, 230)).Single();
        var ru = advice.LaneLine(Language.Russian);
        Assert.Contains("выигрывают 38%", ru);
        Assert.Contains("проигрывают 47%", ru);
        Assert.Contains("Тяжёлая линия", ru);
        Assert.Contains("всухую", ru);
        Assert.Contains("Tough lane", advice.LaneLine(Language.English));
    }

    [Fact]
    public void LaneLine_HiddenWhenFewMatches_AndFavourableLaneSaysPressure()
    {
        var axe = H(2, "axe");
        Assert.Empty(LaneAdvisor.Advise(new[] { axe }, Array.Empty<LaneTip>(), lane: _ => new LaneMatchup(100, 60, 10, 30, 10, 5)));
        var good = LaneAdvisor.Advise(new[] { axe }, Array.Empty<LaneTip>(), lane: _ => new LaneMatchup(600, 300, 120, 180, 60, 20)).Single();
        Assert.Contains("в вашу пользу", good.LaneLine(Language.Russian));
        Assert.DoesNotContain("всухую", good.LaneLine(Language.Russian));
    }

    [Fact]
    public void Database_LoadsLaneMatchupsWithoutWarnings()
    {
        var db = GameDatabase.Load();
        Assert.DoesNotContain(db.Warnings, w => w.Contains("lane_matchups", StringComparison.Ordinal));
        Assert.Null(db.LaneVs(1, 99999));
    }
}
