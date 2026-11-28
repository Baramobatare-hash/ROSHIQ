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
            ProfileRankChartBox.Height = value ? 128 : 64;
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
            SetRankRing(null, L);
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
        _ringCalibrated = calibrated;
        SetRankRing(d.EstimatedRankMean, L);
    }

    private double? _ringMean;
    private string _ringKey = "";
    private bool _ringAnimate;
    private bool _ringCalibrated;   // медаль уже есть — кольцо показывает соперников, а не «сейчас»
    private System.Windows.Threading.DispatcherTimer? _ringTimer;
    private static readonly string[] TierRu = { "", "Рекрут", "Страж", "Рыцарь", "Герой", "Легенда", "Властелин", "Божество", "Титан" };
    private static readonly string[] TierEn = { "", "Herald", "Guardian", "Crusader", "Archon", "Legend", "Ancient", "Divine", "Immortal" };

    /// <summary>Круглая шкала: пять звёзд текущей медали по кругу и процент до следующего ранга в центре.</summary>
    private void SetRankRing(double? mean, Loc L)
    {
        if (mean is not double m) { ProfileRankChartBox.Visibility = Visibility.Collapsed; _ringKey = ""; return; }
        ProfileRankChartBox.Visibility = Visibility.Visible;
        var key = m.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + L.Language + _large + _ringCalibrated;
        if (key == _ringKey) return;
        _ringKey = key;
        _ringMean = m;
        _ringAnimate = true;
        DrawRankChart();
    }

    private void ProfileRankChart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawRankChart();

    private void DrawRankChart()
    {
        if (_ringMean is not double mean) return;
        double h = ProfileRankChart.ActualHeight;
        if (h < 20) return;
        var (cur, next, progress) = Roshiq.Data.RankEstimator.Progress(mean);
        if (_ringAnimate && Motion.Enabled)
        {
            // шкала «набегает» от нуля до текущего значения
            _ringAnimate = false;
            _ringTimer?.Stop();
            var start = DateTime.UtcNow;
            _ringTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _ringTimer.Tick += (_, _) =>
            {
                double t = Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / 1100);
                double eased = 1 - Math.Pow(1 - t, 3);
                DrawRing(cur, next, progress, eased);
                if (t >= 1) _ringTimer!.Stop();
            };
            _ringTimer.Start();
            DrawRing(cur, next, progress, 0);
            return;
        }
        _ringAnimate = false;
        DrawRing(cur, next, progress, 1);
    }

    /// <summary>
    /// Кольцо из пяти дуг — звёзды текущей медали. Пройденные горят целиком, текущая заполнена на долю пути
    /// до следующего ранга, остальные — серые. В центре процент, справа — «сейчас» и «до следующего».
    /// </summary>
    private void DrawRing(int current, int? next, double progress, double k)
    {
        var c = ProfileRankChart;
        c.Children.Clear();
        double h = c.ActualHeight;
        var L = Loc.Instance;
        double size = h, cx = size / 2, cy = size / 2;
        double thick = _large ? 11 : 7, r = size / 2 - thick / 2 - 3;
        int medal = Math.Clamp(current / 10, 1, 8), star = current >= 80 ? 5 : Math.Clamp(current % 10, 1, 5);
        bool immortal = current >= 80;
        var accent = ChartKit.ColorOf("Accent");
        Point At(double deg) => new(cx + r * Math.Sin(deg * Math.PI / 180), cy - r * Math.Cos(deg * Math.PI / 180));
        System.Windows.Shapes.Path Arc(double from, double to, System.Windows.Media.Brush brush, bool glow)
        {
            var fig = new System.Windows.Media.PathFigure { StartPoint = At(from) };
            fig.Segments.Add(new System.Windows.Media.ArcSegment(At(to), new Size(r, r), 0, to - from > 180,
                System.Windows.Media.SweepDirection.Clockwise, true));
            var g = new System.Windows.Media.PathGeometry();
            g.Figures.Add(fig);
            var p = new System.Windows.Shapes.Path
            {
                Data = g, Stroke = brush, StrokeThickness = thick, IsHitTestVisible = false,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round, StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
            };
            if (glow) p.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = accent, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.8 };
            c.Children.Add(p);
            return p;
        }
        // общий прогресс по медали в «звёздах»: пройденные звёзды + доля текущей; анимируется множителем k
        double filled = immortal ? 5 : (star - 1 + progress) * k;
        const double gap = 9;
        for (int i = 0; i < 5; i++)
        {
            double a0 = i * 72 + gap / 2, a1 = (i + 1) * 72 - gap / 2;
            Arc(a0, a1, Ui.Res("Panel3"), false);
            double part = Math.Clamp(filled - i, 0, 1);
            if (part > 0.01) Arc(a0, a0 + (a1 - a0) * part, Ui.Res("Accent"), true);
        }
        // точка на конце заполнения
        if (!immortal && filled > 0.02)
        {
            int seg = Math.Min(4, (int)Math.Floor(filled));
            double a0 = seg * 72 + gap / 2, a1 = (seg + 1) * 72 - gap / 2;
            var end = At(a0 + (a1 - a0) * Math.Clamp(filled - seg, 0, 1));
            if (k >= 1) ChartKit.PulseDot(c, end, "Accent", _large ? 7 : 5);
        }
        // центр: процент до следующего ранга
        int pct = immortal ? 100 : (int)Math.Round(progress * 100 * k);
        var big = new TextBlock
        {
            Text = pct + "%", FontSize = _large ? 26 : 15, FontWeight = FontWeights.Bold, Foreground = Ui.Res("Text"), IsHitTestVisible = false,
        };
        big.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(big, cx - big.DesiredSize.Width / 2);
        Canvas.SetTop(big, cy - big.DesiredSize.Height / 2 - (_large ? 6 : 0));
        c.Children.Add(big);
        if (_large)
        {
            var sub = ChartKit.Label(c, L.Get("rank_ring_to_next"), 0, 0, "Muted", 10);
            sub.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(sub, cx - sub.DesiredSize.Width / 2);
            Canvas.SetTop(sub, cy + big.DesiredSize.Height / 2 - 6);
        }
        // справа: сейчас и следующий ранг
        string Name(int tier) => Roshiq.Data.Ranks.Name(tier, null, L.Language) ?? "?";
        double tx = size + (_large ? 18 : 10);
        var now = ChartKit.Label(c, L.Format(_ringCalibrated ? "rank_ring_opp" : "rank_ring_now", Name(current)), tx, cy - (_large ? 30 : 18), "Text", _large ? 16 : 12, FontWeights.SemiBold);
        string line2 = immortal ? L.Get("rank_ring_top")
            : L.Format("rank_ring_next", Name(next!.Value), (int)Math.Round(progress * 100));
        ChartKit.Label(c, line2, tx, cy - (_large ? 6 : 0), "Accent", _large ? 13 : 11, FontWeights.SemiBold);
        if (_large && _ringCalibrated)
            ChartKit.Label(c, L.Get("rank_ring_opp_note"), tx, cy + 16, "Muted", 11.5);
        else if (_large)
            ChartKit.Label(c, L.Format("rank_ring_stars", star, 5, (L.Language == Roshiq.Core.Models.Language.English ? TierEn : TierRu)[medal]), tx, cy + 16, "Muted", 11.5);
        ProfileRankChartBox.ToolTip = L.Get("rank_ring_tip");
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
