using System.Windows;
using System.Windows.Controls;
using Roshiq.Core.Localization;
using Roshiq.Core.Models;
using Roshiq.Recommendations;

namespace Roshiq.App.Views;

/// <summary>
/// Профиль игрока: аватар, ник, медаль, примерный рейтинг с графиком, помощник на калибровку и слабое место.
/// Один и тот же блок на главной (компактно) и в разделе «Профиль» (крупно).
/// </summary>
public partial class ProfileSummary : UserControl
{
    public ProfileSummary()
    {
        InitializeComponent();
    }

    /// <summary>Привязан ли Steam — от этого зависит, куда ведёт кнопка рядом.</summary>
    public bool Linked { get; private set; }

    /// <summary>Крупный вид для раздела «Профиль»: больше аватар и ник, выше график.</summary>
    public bool Large
    {
        get => _large;
        set
        {
            _large = value;
            ProfileAvatar.Width = ProfileAvatar.Height = value ? 72 : 40;
            ProfileName.FontSize = value ? 26 : ProfileName.FontSize;
            ProfileRankChartBox.Height = value ? 90 : 38;
        }
    }
    private bool _large;

    private string? _avatarUrl;
    private DateTime _weakCheckedUtc;
    private (string Key, double AvgPct, int Matches)? _weak;

    /// <summary>Карточка профиля: ник, медаль и статистика из привязанного Steam либо приглашение привязать.</summary>
    public void Refresh()
    {
        var L = Loc.Instance;
        var d = App.Host.Settings.Current.Data;
        if (App.Host.SteamAccount is not long account)
        {
            ProfileName.Text = L.Get("dash_steam_link");
            ProfileSub.Text = L.Get("dash_steam_link_hint");
            Linked = false;
            ProfileWeak.Visibility = Visibility.Collapsed;
            ProfileRank.Visibility = Visibility.Collapsed;
            ProfileRankChartBox.Visibility = Visibility.Collapsed;
            ProfileCalib.Visibility = Visibility.Collapsed;
            SetProfileAvatar(null);
            return;
        }
        ProfileName.Text = d.SteamName ?? L.Format("steam_account", account);
        var parts = new List<string>();
        if (Roshiq.Data.Ranks.Name(d.RankTier, d.LeaderboardRank, L.Language) is string rank) parts.Add(rank);
        var stats = App.Host.PlayerHeroes;
        if (stats.Count > 0)
        {
            int games = stats.All.Values.Sum(x => x.Games), wins = stats.All.Values.Sum(x => x.Wins);
            parts.Add(L.Format("dash_steam_games", games, games == 0 ? 0 : (double)wins / games));
            var top = stats.All.Values.OrderByDescending(x => x.Games).FirstOrDefault();
            if (top != null && App.Host.Database.FindHero(top.HeroId) is { } hero)
                parts.Add(L.Format("dash_steam_top", hero.DisplayName(L.Language), top.Games));
        }
        if (d.PublicMatchData == false) parts.Add(L.Get("steam_private_short"));
        ProfileSub.Text = parts.Count > 0 ? string.Join(" · ", parts) : L.Get("dash_steam_no_stats");
        RefreshRankEstimate(d, L);
        Linked = true;
        // что проседает чаще всего по последним разборам матчей
        if ((DateTime.UtcNow - _weakCheckedUtc).TotalSeconds > 30)
        {
            _weakCheckedUtc = DateTime.UtcNow;
            _weak = MatchReviewStore.Weakest(App.Host.Reviews.Recent(10));
        }
        if (_weak is { } w)
        {
            var (ru, en) = MatchReviewBuilder.BenchmarkNames[w.Key];
            ProfileWeak.Text = L.Format("dash_weak", L.Language == Roshiq.Core.Models.Language.English ? en : ru, (int)Math.Round((1 - w.AvgPct) * 100), w.Matches);
            ProfileWeak.Visibility = Visibility.Visible;
        }
        else ProfileWeak.Visibility = Visibility.Collapsed;
        SetProfileAvatar(d.AllowOnlineStats ? d.SteamAvatarUrl : null);
    }

    /// <summary>
    /// Примерный рейтинг по средней медали соперников. Без медали (калибровка не пройдена) — главная строка
    /// «Примерный рейтинг», с медалью — справочно «Соперники в среднем».
    /// </summary>
    private void RefreshRankEstimate(Roshiq.Core.Settings.DataSettings d, Loc L)
    {
        RefreshCalibrationHeroes(d, L);
        if (d.EstimatedRankLow is not int lo || d.EstimatedRankHigh is not int hi || d.EstimatedRankMatches is not int n)
        {
            bool noMedal = Roshiq.Data.Ranks.Name(d.RankTier, d.LeaderboardRank, L.Language) == null;
            ProfileRank.Text = L.Get("dash_rank_estimate_none");
            // закрытый профиль уже подписан выше — «после 5 матчей» тут было бы неправдой
            ProfileRank.Visibility = noMedal && d.AllowOnlineStats && d.ProfileCheckedUtc != null && d.PublicMatchData != false
                ? Visibility.Visible : Visibility.Collapsed;
            ProfileRank.Foreground = Ui.Res("Muted");
            ProfileRank.ToolTip = null;
            SetRankChart(null, L);
            return;
        }
        var range = Roshiq.Data.RankEstimator.Range(new Roshiq.Data.RankEstimate(lo, hi, n, d.EstimatedRankSolo), L.Language);
        bool calibrated = Roshiq.Data.Ranks.Name(d.RankTier, d.LeaderboardRank, L.Language) != null;
        var text = L.Format(calibrated ? "dash_rank_opponents" : "dash_rank_estimate", range, n);
        // как было 20 рейтинговых матчей назад — растёт оценка или падает
        if (d.EstimatedRankPrevLow is int plo && d.EstimatedRankPrevHigh is int phi)
        {
            int dir = Roshiq.Data.RankEstimator.Direction(plo, phi, lo, hi);
            var prev = Roshiq.Data.RankEstimator.Range(new Roshiq.Data.RankEstimate(plo, phi, 0, false), L.Language);
            text += " · " + L.Format(dir > 0 ? "dash_rank_trend_up" : dir < 0 ? "dash_rank_trend_down" : "dash_rank_trend_same", prev);
        }
        ProfileRank.Text = text;
        ProfileRank.Foreground = Ui.Res(calibrated ? "Muted" : "Text");
        ProfileRank.ToolTip = L.Get(d.EstimatedRankSolo ? "dash_rank_estimate_tip" : "dash_rank_estimate_tip_party");
        ProfileRank.Visibility = Visibility.Visible;
        SetRankChart(d.EstimatedRankHistory, L);
    }

    private List<(int Low, int High)> _rankChart = new();
    private string _rankChartKey = "";

    /// <summary>График оценки по окнам из 20 рейтинговых матчей: линия по середине вилки, полоса — сама вилка.</summary>
    private void SetRankChart(List<int>? history, Loc L)
    {
        var points = history?.Select(Roshiq.Data.RankEstimator.Unpack).ToList() ?? new();
        if (points.Count < 3) { ProfileRankChartBox.Visibility = Visibility.Collapsed; _rankChartKey = ""; return; }
        var key = string.Join(",", history!) + L.Language;
        ProfileRankChartBox.Visibility = Visibility.Visible;
        if (key == _rankChartKey) return;
        _rankChartKey = key;
        _rankChart = points;
        _rankAnimate = true;
        ProfileRankChartBox.ToolTip = null;
        DrawRankChart();
    }

    private void ProfileRankChart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawRankChart();

    private bool _rankAnimate;
    private static readonly string[] TierRu = { "", "Рекрут", "Страж", "Рыцарь", "Герой", "Легенда", "Властелин", "Божество", "Титан" };
    private static readonly string[] TierEn = { "", "Herald", "Guardian", "Crusader", "Archon", "Legend", "Ancient", "Divine", "Immortal" };

    /// <summary>
    /// «Лестница медалей»: за графиком — полосы медалей (Страж, Рыцарь…), по ним идёт лента вилки и светящаяся
    /// линия её середины. Точка «сейчас» пульсирует, при наведении — какие матчи и какая вилка.
    /// </summary>
    private void DrawRankChart()
    {
        var c = ProfileRankChart;
        c.Children.Clear();
        double w = c.ActualWidth, h = c.ActualHeight;
        if (_rankChart.Count < 3 || w < 20 || h < 10) return;
        var L = Loc.Instance;
        var steps = _rankChart.Select(p => (Lo: (double)Roshiq.Data.RankEstimator.ToStep(p.Low), Hi: (double)Roshiq.Data.RankEstimator.ToStep(p.High))).ToList();
        double min = steps.Min(s => s.Lo) - 0.6, max = steps.Max(s => s.Hi) + 0.6;
        if (max - min < 4) { double mid = (max + min) / 2; min = mid - 2; max = mid + 2; }
        double padL = _large ? 64 : 4, padR = _large ? 70 : 10, padT = 6, padB = 6;
        double X(int i) => padL + i * (w - padL - padR) / (steps.Count - 1);
        double Y(double s) => padT + (max - s) * (h - padT - padB) / (max - min);

        // полосы медалей: каждая медаль — 5 ступеней; чередуем фон и подписываем медаль слева
        var names = L.Language == Roshiq.Core.Models.Language.English ? TierEn : TierRu;
        for (int tier = (int)Math.Floor(min / 5); tier * 5 <= max; tier++)
        {
            double top = Y(Math.Min(max, tier * 5 + 5 - 0.5)), bottom = Y(Math.Max(min, tier * 5 - 0.5));
            if (bottom - top < 1) continue;
            var band = new System.Windows.Shapes.Rectangle
            {
                Width = w, Height = bottom - top, IsHitTestVisible = false,
                Fill = Ui.Res(tier % 2 == 0 ? "Panel2" : "Panel"), Opacity = 0.55,
            };
            Canvas.SetTop(band, top);
            c.Children.Add(band);
            c.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0, X2 = w, Y1 = bottom, Y2 = bottom, Stroke = Ui.Res("Line"), StrokeThickness = 1,
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 2, 4 }, IsHitTestVisible = false,
            });
            if (_large && tier + 1 < names.Length && bottom - top >= 14)
                ChartKit.Label(c, names[Math.Clamp(tier + 1, 1, 8)], 6, (top + bottom) / 2 - 7, "Faint", 10.5, FontWeights.SemiBold);
        }

        var hiPts = steps.Select((s, i) => new Point(X(i), Y(s.Hi))).ToList();
        var loPts = steps.Select((s, i) => new Point(X(i), Y(s.Lo))).ToList();
        var midPts = steps.Select((s, i) => new Point(X(i), Y((s.Lo + s.Hi) / 2))).ToList();
        var ribbon = ChartKit.Ribbon(hiPts, loPts, "Accent", 0.30);
        c.Children.Add(ribbon);
        var area = ChartKit.Area(midPts, h, "Accent", 0.12);
        c.Children.Add(area);
        var line = ChartKit.Curve(midPts, "Accent", _large ? 2.6 : 2);
        c.Children.Add(line);
        // засечки окон: маленькие ромбы на линии
        if (_large)
            foreach (var p in midPts.Take(midPts.Count - 1))
            {
                var pip = new System.Windows.Shapes.Rectangle
                {
                    Width = 6, Height = 6, Fill = Ui.Res("Panel"), Stroke = Ui.Res("Accent"), StrokeThickness = 1.5, IsHitTestVisible = false,
                    RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new System.Windows.Media.RotateTransform(45),
                };
                Canvas.SetLeft(pip, p.X - 3);
                Canvas.SetTop(pip, p.Y - 3);
                c.Children.Add(pip);
                if (_rankAnimate) ChartKit.FadeIn(pip, 500, 500);
            }
        ChartKit.PulseDot(c, midPts[^1], "Accent", _large ? 9 : 7);
        if (_large)
        {
            var now = Roshiq.Data.RankEstimator.Range(new Roshiq.Data.RankEstimate(_rankChart[^1].Low, _rankChart[^1].High, 0, false), L.Language);
            var label = ChartKit.Label(c, now, w - padR + 12, midPts[^1].Y - 9, "Accent", 11.5, FontWeights.Bold);
            label.MaxWidth = padR - 14;
            label.TextWrapping = TextWrapping.Wrap;
            if (_rankAnimate) ChartKit.FadeIn(label, 900, 500);
        }
        if (_rankAnimate)
        {
            ChartKit.DrawOn(line, 0, 1100);
            ChartKit.FadeIn(ribbon, 300, 900);
            ChartKit.FadeIn(area, 300, 900);
            _rankAnimate = false;
        }

        var xs = midPts.Select(p => p.X).ToList();
        int n = steps.Count;
        ChartKit.Hover(c, x =>
        {
            int i = ChartKit.Nearest(xs, x);
            int skip = (n - 1 - i) * 10;
            var range = Roshiq.Data.RankEstimator.Range(new Roshiq.Data.RankEstimate(_rankChart[i].Low, _rankChart[i].High, 0, false), L.Language);
            var when = i == n - 1 ? L.Get("rank_chart_recent") : L.Format("rank_chart_window", skip + 1, skip + 20);
            return new ChartKit.Probe(xs[i], when + "\n" + range, new[] { (midPts[i].Y, "Accent") });
        });
    }

    /// <summary>Без медали — герои, на которых у вас лучший надёжный винрейт: их и стоит брать на калибровку.</summary>
    private void RefreshCalibrationHeroes(Roshiq.Core.Settings.DataSettings d, Loc L)
    {
        bool calibrated = Roshiq.Data.Ranks.Name(d.RankTier, d.LeaderboardRank, L.Language) != null;
        var heroes = calibrated ? Array.Empty<Roshiq.Data.PlayerHeroStat>()
            : Roshiq.Data.RankEstimator.CalibrationHeroes(App.Host.PlayerHeroes.All.Values, DateTime.UtcNow);
        string? Line(IEnumerable<Roshiq.Data.PlayerHeroStat> list) => list
            .Select(s => App.Host.Database.FindHero(s.HeroId) is { } h
                ? L.Format("dash_calib_hero", h.DisplayName(L.Language), s.WinRate, s.Games) : null)
            .Where(x => x != null)
            .ToList() is { Count: > 0 } names ? string.Join(", ", names) : null;
        var lines = new List<string>();
        if (Line(heroes) is string best) lines.Add(L.Format("dash_calib", best));
        if (!calibrated)
        {
            var all = App.Host.PlayerHeroes.All.Values;
            if (Roshiq.Data.RankEstimator.BestRole(all, id => App.Host.Database.FindHero(id)?.Positions.FirstOrDefault() is Position p && p != 0 ? p : null, DateTime.UtcNow) is { } role)
                lines.Add(L.Format("dash_calib_role", L.Get("calib_role_" + role.Group), role.WinRate, role.Games));
            if (Line(Roshiq.Data.RankEstimator.AvoidHeroes(all, DateTime.UtcNow)) is string avoid)
                lines.Add(L.Format("dash_calib_avoid", avoid));
        }
        if (lines.Count == 0) { ProfileCalib.Visibility = Visibility.Collapsed; return; }
        ProfileCalib.Text = string.Join("\n", lines);
        ProfileCalib.ToolTip = L.Get("dash_calib_tip");
        ProfileCalib.Visibility = Visibility.Visible;
    }

    private void SetProfileAvatar(string? url)
    {
        if (url == _avatarUrl) return;
        _avatarUrl = url;
        System.Windows.Media.ImageSource? img = null;
        if (url != null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var b = new System.Windows.Media.Imaging.BitmapImage();
                b.BeginInit();
                b.UriSource = new Uri(url);
                b.DecodePixelWidth = 80;
                b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                b.EndInit();
                img = b;
            }
            catch { img = null; }
        }
        ProfileAvatar.Background = img != null
            ? new System.Windows.Media.ImageBrush(img) { Stretch = System.Windows.Media.Stretch.UniformToFill }
            : (System.Windows.Media.Brush)FindResource("Panel3");
        ProfileAvatarIcon.Visibility = img != null ? Visibility.Collapsed : Visibility.Visible;
    }

}
