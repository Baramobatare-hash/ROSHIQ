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
        var first = Roshiq.Data.RankEstimator.Range(new Roshiq.Data.RankEstimate(points[0].Low, points[0].High, 0, false), L.Language);
        var last = Roshiq.Data.RankEstimator.Range(new Roshiq.Data.RankEstimate(points[^1].Low, points[^1].High, 0, false), L.Language);
        ProfileRankChartBox.ToolTip = L.Format("dash_rank_chart_tip", points.Count * 10 + 10, first, last);
        DrawRankChart();
    }

    private void ProfileRankChart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawRankChart();

    private void DrawRankChart()
    {
        var c = ProfileRankChart;
        c.Children.Clear();
        double w = c.ActualWidth, h = c.ActualHeight;
        if (_rankChart.Count < 3 || w < 20 || h < 10) return;
        var steps = _rankChart.Select(p => (Lo: (double)Roshiq.Data.RankEstimator.ToStep(p.Low), Hi: (double)Roshiq.Data.RankEstimator.ToStep(p.High))).ToList();
        double min = steps.Min(s => s.Lo), max = steps.Max(s => s.Hi);
        if (max - min < 4) { double mid = (max + min) / 2; min = mid - 2; max = mid + 2; }
        const double pad = 4;
        double X(int i) => pad + i * (w - 2 * pad) / (steps.Count - 1);
        double Y(double s) => pad + (max - s) * (h - 2 * pad) / (max - min);
        var accent = Ui.Res("Accent");
        // полоса вилки
        var band = new System.Windows.Shapes.Polygon { Fill = accent, Opacity = 0.32 };
        for (int i = 0; i < steps.Count; i++) band.Points.Add(new Point(X(i), Y(steps[i].Hi)));
        for (int i = steps.Count - 1; i >= 0; i--) band.Points.Add(new Point(X(i), Y(steps[i].Lo)));
        c.Children.Add(band);
        // линия середины
        var line = new System.Windows.Shapes.Polyline { Stroke = accent, StrokeThickness = 2, StrokeLineJoin = System.Windows.Media.PenLineJoin.Round };
        for (int i = 0; i < steps.Count; i++) line.Points.Add(new Point(X(i), Y((steps[i].Lo + steps[i].Hi) / 2)));
        c.Children.Add(line);
        // последняя точка — сейчас
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = accent };
        Canvas.SetLeft(dot, X(steps.Count - 1) - 3.5);
        Canvas.SetTop(dot, Y((steps[^1].Lo + steps[^1].Hi) / 2) - 3.5);
        c.Children.Add(dot);
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
