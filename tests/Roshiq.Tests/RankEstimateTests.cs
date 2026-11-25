using Roshiq.Core.Models;
using Roshiq.Data;
using Xunit;

namespace Roshiq.Tests;

/// <summary>Примерный рейтинг по средней медали соперников (average_rank OpenDota).</summary>
public class RankEstimateTests
{
    [Theory]
    [InlineData(11, 0)]
    [InlineData(25, 9)]
    [InlineData(31, 10)]
    [InlineData(75, 34)]
    [InlineData(80, 35)]
    public void Step_RoundTrips(int tier, int step)
    {
        Assert.Equal(step, RankEstimator.ToStep(tier));
        Assert.Equal(tier, RankEstimator.FromStep(step));
    }

    [Fact]
    public void Estimate_UsesSoloMatches_WhenEnough()
    {
        // реальные 20 матчей: соло — Страж 5–Рыцарь 1, в пати — Герой/Легенда
        var rows = new (int, int)[]
        {
            (31,1),(25,2),(25,1),(25,1),(25,1),(31,1),(25,1),(31,1),(31,1),(41,3),
            (34,5),(34,5),(41,3),(31,1),(25,1),(31,1),(31,1),(25,1),(24,1),(25,1)
        };
        var e = RankEstimator.Estimate(rows);
        Assert.NotNull(e);
        Assert.True(e!.SoloOnly);
        Assert.Equal(15, e.Matches);
        Assert.Equal(25, e.Low);
        Assert.Equal(31, e.High);
        Assert.Equal("Страж 5 – Рыцарь 1", RankEstimator.Range(e, Language.Russian));
        Assert.Equal("Guardian 5 – Crusader 1", RankEstimator.Range(e, Language.English));
    }

    [Fact]
    public void Estimate_FallsBackToParty_AndNeedsFiveMatches()
    {
        Assert.Null(RankEstimator.Estimate(new (int, int)[] { (52, 1), (53, 1), (0, 1), (54, 1) }));
        var e = RankEstimator.Estimate(new (int, int)[] { (52, 2), (53, 2), (54, 1), (52, 3), (52, 2), (0, 1) });
        Assert.NotNull(e);
        Assert.False(e!.SoloOnly);
        Assert.Equal(5, e.Matches);
        Assert.Equal(52, e.Low);
        Assert.Equal(53, e.High);
        Assert.Equal("Легенда 2 – Легенда 3", RankEstimator.Range(e, Language.Russian));
    }
}
