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

    [Fact]
    public void Trend_ComparesWithPrevious20Matches()
    {
        // свежие 20 — Рыцарь 1–2, прошлые 20 — Страж 2–3
        var rows = Enumerable.Repeat((31, 1), 10).Concat(Enumerable.Repeat((32, 1), 10))
            .Concat(Enumerable.Repeat((22, 1), 10)).Concat(Enumerable.Repeat((23, 1), 10)).ToList();
        var now = RankEstimator.Estimate(rows)!;
        var prev = RankEstimator.Estimate(rows, skip: 20)!;
        Assert.Equal((31, 32), (now.Low, now.High));
        Assert.Equal((22, 23), (prev.Low, prev.High));
        Assert.Equal(1, RankEstimator.Direction(prev.Low, prev.High, now.Low, now.High));
        Assert.Equal(-1, RankEstimator.Direction(now.Low, now.High, prev.Low, prev.High));
        Assert.Equal(0, RankEstimator.Direction(31, 32, 31, 31));
    }

    [Fact]
    public void CalibrationHeroes_PreferReliableWinRate()
    {
        var now = new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc);
        long recent = new DateTimeOffset(now.AddDays(-10)).ToUnixTimeSeconds();
        long old = new DateTimeOffset(now.AddDays(-500)).ToUnixTimeSeconds();
        var stats = new[]
        {
            new PlayerHeroStat(1, 10, 8, recent),   // 80% на 10 играх
            new PlayerHeroStat(2, 60, 39, recent),  // 65% на 60 играх — надёжнее
            new PlayerHeroStat(3, 9, 9, recent),    // меньше 10 игр
            new PlayerHeroStat(4, 80, 60, old),     // давно не играл
            new PlayerHeroStat(5, 50, 20, recent),  // 40% — не советуем
        };
        var picks = RankEstimator.CalibrationHeroes(stats, now);
        Assert.Equal(new[] { 2, 1 }, picks.Select(p => p.HeroId).ToArray());
    }
}
