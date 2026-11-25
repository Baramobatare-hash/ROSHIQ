using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Roshiq.Core.Localization;
using Roshiq.Core.Models;
using Roshiq.Recommendations;

namespace Roshiq.App.Views;

/// <summary>
/// Итоги недели в разделе «Профиль»: игры, винрейт и его изменение к прошлой неделе, KDA, лучший и худший герой,
/// серия, главная повторяющаяся ошибка с динамикой и «неделя по дням» — каждый матч отдельной капсулой.
/// </summary>
public partial class WeekReport : UserControl
{
    public WeekReport()
    {
        InitializeComponent();
    }

    private IReadOnlyList<MatchRecord> _all = Array.Empty<MatchRecord>();
    private string _key = "";
    private bool _animate;

    public void Refresh(IReadOnlyList<MatchRecord> all)
    {
        var L = Loc.Instance;
        _all = all;
        var key = all.Count + "|" + (all.Count > 0 ? all.Max(m => m.StartedUtc).Ticks : 0) + "|" + L.Language + "|" + DateTime.Now.Date.Ticks;
        bool changed = key != _key;
        _key = key;
        var w = PlayerReport.Week(all, DateTime.UtcNow);
        Chips.Children.Clear();
        GamesUnit.Text = L.Get("wr_games_unit");
        if (w.Games == 0)
        {
            GamesValue.Text = "0";
            WinValue.Text = "—";
            WinDelta.Text = "";
            KdaLine.Text = L.Get("week_none");
        }
        else
        {
            if (changed) Motion.CountUp(GamesValue, w.Games, "0"); else GamesValue.Text = w.Games.ToString(CultureInfo.InvariantCulture);
            int wr = (int)Math.Round(w.WinRate!.Value * 100);
            if (changed) Motion.CountUp(WinValue, wr, "0", "%"); else WinValue.Text = wr + "%";
            WinValue.Foreground = Ui.Res(wr >= 50 ? "Accent" : "Text");
            if (w.PrevWinRate is double prev)
            {
                int delta = (int)Math.Round((w.WinRate!.Value - prev) * 100);
                WinDelta.Text = (delta > 0 ? "▲ +" : delta < 0 ? "▼ " : "• ") + delta + L.Get("wr_pp");
                WinDelta.Foreground = Ui.Res(delta > 0 ? "Accent" : delta < 0 ? "Red" : "Muted");
            }
            else { WinDelta.Text = L.Get("wr_no_prev"); WinDelta.Foreground = Ui.Res("Muted"); }
            KdaLine.Text = L.Format("week_kda", w.AvgKills.ToString("0.#"), w.AvgDeaths.ToString("0.#"), w.AvgAssists.ToString("0.#")) + " · KDA " + w.Kda.ToString("0.0");
            string HeroName(int id) => App.Host.Database.FindHero(id)?.DisplayName(L.Language) ?? "?";
            if (w.Best is { } best) Chips.Children.Add(Ui.Chip(L.Format("week_best", HeroName(best.HeroId), best.Wins, best.Games), "AccentSoft", "Accent"));
            if (w.Worst is { } worst) Chips.Children.Add(Ui.Chip(L.Format("week_worst", HeroName(worst.HeroId), worst.Wins, worst.Games)));
        }
        if (w.Streak >= 3) Chips.Children.Add(Ui.Chip(L.Format("week_streak_win", w.Streak), "AccentSoft", "Accent"));
        else if (w.Streak <= -3) Chips.Children.Add(Ui.Chip(L.Format("week_streak_loss", -w.Streak), "WarningSoft", "Warning"));

        var top = PlayerReport.Recurring(all).FirstOrDefault();
        if (top != null)
        {
            Mistake.Text = top.Text(L.Language);
            MistakeSub.Text = L.Format("rec_line", top.Count, top.Of, (int)Math.Round(top.Rate * 100)) + " · "
                              + L.Get(top.Trend < 0 ? "rec_better" : top.Trend > 0 ? "rec_worse" : "rec_same");
            MistakeSub.Foreground = Ui.Res(top.Trend < 0 ? "Accent" : top.Trend > 0 ? "Warning" : "Muted");
        }
        else
        {
            Mistake.Text = L.Get("week_mistake_none");
            MistakeSub.Text = "";
        }
        if (changed) { _animate = true; DrawDays(); }
    }

    private void Days_SizeChanged(object sender, SizeChangedEventArgs e) => DrawDays();

    /// <summary>
    /// Неделя по дням: на каждый день — стопка капсул, одна капсула — один матч (победа светится цветом темы,
    /// поражение — приглушённо-красная). Капсулы «вырастают» по очереди; при наведении — итог дня.
    /// </summary>
    private void DrawDays()
    {
        var c = Days;
        c.Children.Clear();
        double w = c.ActualWidth, h = c.ActualHeight;
        if (w < 60 || h < 40) return;
        var L = Loc.Instance;
        var culture = L.Language == Roshiq.Core.Models.Language.English ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("ru-RU");
        var today = DateTime.Now.Date;
        var days = Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6)).ToList();
        var byDay = days.Select(d => _all.Where(m => m.StartedUtc.ToLocalTime().Date == d && m.Result != MatchResult.Unknown)
            .OrderBy(m => m.StartedUtc).ToList()).ToList();
        int maxGames = Math.Max(4, byDay.Max(g => g.Count));
        const double labelH = 18, gap = 3;
        double colW = w / 7, pillW = Math.Min(34, colW * 0.62);
        double pillH = Math.Min(16, (h - labelH - 4 - gap * (maxGames - 1)) / maxGames);
        var accent = ChartKit.ColorOf("Accent");
        int order = 0;
        var xs = new List<double>();
        for (int d = 0; d < 7; d++)
        {
            double cx = colW * d + colW / 2;
            xs.Add(cx);
            bool isToday = d == 6;
            ChartKit.Label(c, days[d].ToString("ddd", culture), cx - 10, h - labelH + 2, isToday ? "Accent" : "Faint", 10.5, isToday ? FontWeights.Bold : FontWeights.Normal);
            // пустой день — тонкая пунктирная «подставка»
            if (byDay[d].Count == 0)
            {
                var stub = new Line { X1 = cx - pillW / 2, X2 = cx + pillW / 2, Y1 = h - labelH - 2, Y2 = h - labelH - 2, Stroke = Ui.Res("Line"), StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 2, 2 }, IsHitTestVisible = false };
                c.Children.Add(stub);
                continue;
            }
            for (int k = 0; k < byDay[d].Count; k++)
            {
                var m = byDay[d][k];
                bool win = m.Result == MatchResult.Win;
                double y = h - labelH - 4 - (k + 1) * pillH - k * gap;
                var pill = new Rectangle
                {
                    Width = pillW, Height = pillH, RadiusX = pillH / 2, RadiusY = pillH / 2, IsHitTestVisible = false,
                    Fill = win ? new LinearGradientBrush(Color.FromArgb(255, accent.R, accent.G, accent.B), Color.FromArgb(170, accent.R, accent.G, accent.B), 90)
                               : Ui.Res("Red"),
                    Opacity = win ? 1 : 0.55,
                    RenderTransformOrigin = new Point(0.5, 1), RenderTransform = new ScaleTransform(1, 1),
                };
                if (win) pill.Effect = new DropShadowEffect { Color = accent, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.6 };
                Canvas.SetLeft(pill, cx - pillW / 2);
                Canvas.SetTop(pill, y);
                c.Children.Add(pill);
                if (_animate && Motion.Enabled)
                {
                    var st = (ScaleTransform)pill.RenderTransform;
                    st.ScaleY = 0;
                    var a = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380))
                    {
                        BeginTime = TimeSpan.FromMilliseconds(60 * order++),
                        EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 },
                    };
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
                }
            }
        }
        _animate = false;
        int total = byDay.Sum(g => g.Count);
        DaysNote.Text = total == 0 ? L.Get("week_none") : L.Get("wr_days_hint");
        ChartKit.Hover(c, x =>
        {
            int i = ChartKit.Nearest(xs, x);
            var g = byDay[i];
            string day = days[i].ToString("dddd, d MMM", culture);
            string text = g.Count == 0 ? day + "\n" + L.Get("wr_day_empty")
                : day + "\n" + L.Format("wr_day_line", g.Count, g.Count(m => m.Result == MatchResult.Win), g.Count(m => m.Result == MatchResult.Loss));
            double topY = h - labelH - 4 - g.Count * pillH - Math.Max(0, g.Count - 1) * gap;
            return new ChartKit.Probe(xs[i], text, new[] { (Math.Max(0, topY - 6), g.Count > 0 && g.Count(m => m.Result == MatchResult.Win) * 2 >= g.Count ? "Accent" : "Muted") });
        });
    }
}
