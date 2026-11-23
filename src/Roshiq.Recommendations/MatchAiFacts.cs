using System.Globalization;
using Roshiq.Core.Models;

namespace Roshiq.Recommendations;

/// <summary>
/// 0.26.0: факты разбора матча для ИИ-объяснения. Только то, что уже посчитал разбор (цифры, выводы, эпизоды),
/// без ников, Steam ID и ID игроков: нейросеть получает цифры и объясняет их словами.
/// </summary>
public static class MatchAiFacts
{
    public static Dictionary<string, object?> Build(MatchReview r, Func<int, string> heroName, Func<string, string> itemName)
    {
        var me = r.Players.FirstOrDefault(p => p.IsYou);
        bool? won = me != null && r.RadiantWin is bool rw ? rw == me.IsRadiant : null;
        var f = new Dictionary<string, object?>
        {
            ["hero"] = heroName(r.HeroId),
            ["role"] = RoleName(r.Position),
            ["result"] = won == true ? "победа" : won == false ? "поражение" : "неизвестно",
            ["duration"] = MatchReviewBuilder.Clock(r.Duration),
        };
        if (me != null)
        {
            f["kda"] = $"{me.Kills}/{me.Deaths}/{me.Assists}";
            f["gpm"] = me.Gpm;
            f["xpm"] = me.Xpm;
            f["last_hits"] = me.LastHits;
            f["denies"] = me.Denies;
            f["net_worth"] = me.NetWorth;
            f["level"] = me.Level;
            f["hero_damage"] = me.HeroDamage;
            f["tower_damage"] = me.TowerDamage;
            f["healing"] = me.HeroHealing;
            f["allies"] = r.Players.Where(p => !p.IsYou && p.IsRadiant == me.IsRadiant).Select(p => heroName(p.HeroId)).ToList();
            f["enemies"] = r.Players.Where(p => p.IsRadiant != me.IsRadiant).Select(p => heroName(p.HeroId)).ToList();
        }
        if (r.LastHits10 is int lh) f["last_hits_10min"] = r.LastHits10Target is int t ? $"{lh} (ориентир для роли {t})" : lh.ToString(CultureInfo.InvariantCulture);
        if (r.LaneEfficiency is double eff) f["lane_efficiency_pct"] = (int)Math.Round(eff);
        if (r.Benchmarks.Count > 0)
            f["percentile_vs_same_hero"] = r.Benchmarks.ToDictionary(
                b => MatchReviewBuilder.BenchmarkNames.TryGetValue(b.Key, out var n) ? n.Ru : b.Key,
                b => (object)$"лучше, чем у {(int)Math.Round(b.Pct * 100)}% игроков");
        if (r.GoldAdv.Count > 2)
        {
            var gold = new Dictionary<string, string>();
            foreach (var m in new[] { 10, 20, 30 })
                if (m < r.GoldAdv.Count) gold[m + " мин"] = Lead(r.GoldAdv[m]);
            gold["конец"] = Lead(r.GoldAdv[^1]);
            f["team_gold_by_minute"] = gold;
        }
        if (r.Items.Count > 0)
            f["item_timings"] = r.Items.Select(i => $"{itemName(i.ItemKey)} в {MatchReviewBuilder.Clock(i.Yours)}"
                + (i.Norm is int n ? $" (обычно {MatchReviewBuilder.Clock(n)})" : "")).ToList();
        if (r.Killers.Count > 0)
            f["killed_by"] = r.Killers.Take(3).Select(k => $"{heroName(k.HeroId)} ×{k.Count}").ToList();
        if (r.Timeline.Count > 0)
        {
            var deaths = r.Timeline.Where(e => e.Kind == "death").ToList();
            f["deaths"] = deaths.Select(e => $"{MatchReviewBuilder.Clock(e.Time)} {e.Ru}").ToList();
            f["key_events"] = MatchTimeline.Key(r.Timeline).Where(e => e.Kind != "death")
                .Select(e => $"{MatchReviewBuilder.Clock(e.Time)} {e.Ru}").ToList();
        }
        if (r.Observers.Count > 0 || r.Sentries > 0)
            f["wards"] = $"обсерверов {r.Observers.Count}, сентри {r.Sentries}, снято врагами {r.Observers.Count(w => w.Dewarded)}";
        if (r.Insights.Count > 0)
            f["analyzer_findings"] = r.Insights.OrderBy(i => i.Severity).Select(i => (i.Severity < 0 ? "минус: " : "плюс: ") + i.Ru).ToList();
        if (!r.Parsed) f["note"] = "запись матча не разобрана: смертей, золота по минутам и вардов нет";
        return f;
    }

    public static string RoleName(Position? p) => p switch
    {
        Position.Carry => "керри (позиция 1)",
        Position.Mid => "мид (позиция 2)",
        Position.Offlane => "оффлейн (позиция 3)",
        Position.SoftSupport => "саппорт (позиция 4)",
        Position.HardSupport => "саппорт (позиция 5)",
        _ => "неизвестна",
    };

    private static string Lead(int v) => v >= 0
        ? $"твоя команда впереди на {v.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"))} золота"
        : $"твоя команда отстаёт на {(-v).ToString("N0", CultureInfo.GetCultureInfo("ru-RU"))} золота";
}
