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

    [Fact]
    public void History_GoesFromOldToNew()
    {
        // 50 матчей, новые первыми: свежие 20 — Рыцарь 1, остальные — Страж 2
        var rows = Enumerable.Repeat((31, 1), 20).Concat(Enumerable.Repeat((22, 1), 30)).ToList();
        var h = RankEstimator.History(rows);
        Assert.Equal(4, h.Count);                 // окна: 0–19, 10–29, 20–39, 30–49
        Assert.Equal(22, h[0].Low);               // самое старое
        Assert.Equal(31, h[^1].Low);              // самое новое
        Assert.Equal((25, 31), RankEstimator.Unpack(RankEstimator.Pack(new RankEstimate(25, 31, 20, true))));
    }

    [Fact]
    public void AvoidHeroes_OnlyReliableLosses()
    {
        var now = new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc);
        long recent = new DateTimeOffset(now.AddDays(-5)).ToUnixTimeSeconds();
        var stats = new[]
        {
            new PlayerHeroStat(1, 40, 10, recent),  // 25% на 40 играх — не брать
            new PlayerHeroStat(2, 10, 3, recent),   // 30% на 10 играх — может быть случайностью
            new PlayerHeroStat(3, 50, 25, recent),  // 50% — нормально
        };
        Assert.Equal(new[] { 1 }, RankEstimator.AvoidHeroes(stats, now).Select(s => s.HeroId).ToArray());
    }

    [Fact]
    public void BestRole_GroupsHeroesByMainPosition()
    {
        var now = new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc);
        long recent = new DateTimeOffset(now.AddDays(-5)).ToUnixTimeSeconds();
        var pos = new Dictionary<int, Position> { [1] = Position.Carry, [2] = Position.HardSupport, [3] = Position.SoftSupport, [4] = Position.Mid };
        var stats = new[]
        {
            new PlayerHeroStat(1, 60, 27, recent),  // керри 45%
            new PlayerHeroStat(2, 30, 19, recent),  // саппорты 4 и 5 вместе: 70 игр, 44 победы — 63%
            new PlayerHeroStat(3, 40, 25, recent),
            new PlayerHeroStat(4, 5, 5, recent),    // мид — мало игр, не считается
        };
        var role = RankEstimator.BestRole(stats, id => pos.TryGetValue(id, out var p) ? p : null, now);
        Assert.NotNull(role);
        Assert.Equal(4, role!.Value.Group);
        Assert.Equal(70, role.Value.Games);
        Assert.Null(RankEstimator.BestRole(stats.Take(1), id => pos[id], now));
    }

    [Fact]
    public void Progress_ToNextRank()
    {
        // 15 соло: 1× Страж 4 (8), 7× Страж 5 (9), 7× Рыцарь 1 (10) → средняя 9.4
        var rows = new (int, int)[] { (24, 1) }.Concat(Enumerable.Repeat((25, 1), 7)).Concat(Enumerable.Repeat((31, 1), 7)).ToList();
        var e = RankEstimator.Estimate(rows)!;
        Assert.Equal(9.4, e.Mean, 3);
        var (cur, next, p) = RankEstimator.Progress(e.Mean);
        Assert.Equal(25, cur);          // Страж 5
        Assert.Equal(31, next);         // следующий — Рыцарь 1
        Assert.Equal(0.4, p, 3);        // 40% пути
        Assert.Equal((80, (int?)null, 1.0), RankEstimator.Progress(35));
    }
}
