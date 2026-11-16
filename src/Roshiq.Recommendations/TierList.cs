using Roshiq.Core.Models;
using Roshiq.Data;

namespace Roshiq.Recommendations;

/// <summary>Группа рангов для тир-листа.</summary>
public enum RankBracket { All, Low, Mid, High, Immortal, Pro }

/// <param name="Popular">0.24.1: «в мете» — героя берут заметно чаще остальных на этой позиции (верхняя четверть по частоте) и он не проигрывает.</param>
public sealed record TierEntry(Hero Hero, string Tier, double WinRate, double PickRate, int Games, bool Popular = false);

/// <summary>
/// 0.23.0: тир-лист по позиции — доля побед героя в выбранных рангах по данным OpenDota (/heroStats: «недавние матчи»,
/// точный период OpenDota не называет).
/// OpenDota не делит статистику по позициям: герой попадает в позицию по нашей базе героев, а процент — общий по всем его матчам.
/// Редкие герои сглажены к 50% (+100 игр по 50%), чтобы 8 побед из 10 не выглядели «S-тиром».
/// </summary>
public static class TierList
{
    public static readonly string[] Tiers = { "S", "A", "B", "C", "D" };
    /// <summary>Минимальная доля матчей с героем, чтобы попасть в список (паблики): реже 0,5% — данных мало.</summary>
    public const double MinPickRate = 0.005;
    public const int MinProGames = 10;
    private const double Prior = 100;

    public static IReadOnlyList<int> Brackets(RankBracket b) => b switch
    {
        RankBracket.Low => new[] { 0, 1, 2 },
        RankBracket.Mid => new[] { 3, 4 },
        RankBracket.High => new[] { 5, 6 },
        RankBracket.Immortal => new[] { 7 },
        _ => new[] { 0, 1, 2, 3, 4, 5, 6, 7 }
    };

    /// <summary>Группа по медали из профиля (rank_tier 54 → Легенда → Mid). Без медали — все ранги.</summary>
    public static RankBracket FromRankTier(int? rankTier) => (rankTier / 10) switch
    {
        1 or 2 or 3 => RankBracket.Low,
        4 or 5 => RankBracket.Mid,
        6 or 7 => RankBracket.High,
        8 => RankBracket.Immortal,
        _ => RankBracket.All
    };

    public static string TierFor(double winRate) => winRate switch
    {
        >= 0.53 => "S",
        >= 0.515 => "A",
        >= 0.50 => "B",
        >= 0.485 => "C",
        _ => "D"
    };

    public static List<TierEntry> Build(HeroTierStats stats, IEnumerable<Hero> heroes, Position position, RankBracket bracket)
    {
        var byId = stats.Rows.ToDictionary(r => r.HeroId);
        var idx = Brackets(bracket);
        // всего матчей в выбранных рангах: каждый матч — 10 выборов героев
        double totalMatches = bracket == RankBracket.Pro
            ? stats.Rows.Sum(r => r.ProPick) / 10.0
            : stats.Rows.Sum(r => idx.Sum(i => r.Picks[i])) / 10.0;
        if (totalMatches <= 0) return new List<TierEntry>();

        var list = new List<TierEntry>();
        foreach (var h in heroes.Where(h => h.PlaysPosition(position)))
        {
            if (!byId.TryGetValue(h.Id, out var r)) continue;
            int games = bracket == RankBracket.Pro ? r.ProPick : idx.Sum(i => r.Picks[i]);
            int wins = bracket == RankBracket.Pro ? r.ProWin : idx.Sum(i => r.Wins[i]);
            double pick = games / totalMatches;
            if (bracket == RankBracket.Pro ? games < MinProGames : pick < MinPickRate) continue;
            double wr = (wins + Prior * 0.5) / (games + Prior);
            list.Add(new TierEntry(h, TierFor(wr), wr, pick, games));
        }
        return MarkPopular(list).OrderByDescending(e => e.WinRate).ToList();
    }

    /// <summary>Минимальная доля побед, чтобы популярного героя назвать «в мете»: часто берут, но стабильно проигрывают — это не мета.</summary>
    public const double PopularMinWinRate = 0.495;

    /// <summary>
    /// 0.24.1: отметить героев «в мете» — частота выбора в верхней четверти списка позиции (и не меньше чем в полтора раза выше медианы)
    /// при доле побед от <see cref="PopularMinWinRate"/>. Тир по-прежнему считается только по доле побед.
    /// </summary>
    public static List<TierEntry> MarkPopular(List<TierEntry> list)
    {
        if (list.Count < 4) return list;
        var picks = list.Select(e => e.PickRate).OrderBy(p => p).ToList();
        double median = picks[picks.Count / 2];
        double q75 = picks[(int)Math.Floor((picks.Count - 1) * 0.75)];
        double threshold = Math.Max(q75, median * 1.5);
        return list.Select(e => e with { Popular = e.PickRate >= threshold && e.WinRate >= PopularMinWinRate }).ToList();
    }
}
