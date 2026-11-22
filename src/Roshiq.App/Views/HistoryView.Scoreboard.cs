using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Roshiq.Core.Localization;
using Roshiq.Recommendations;

namespace Roshiq.App.Views;

/// <summary>
/// 0.25.0: сводка матча в духе STRATZ — крупный счёт сверху, под ним две таблицы (Свет и Тьма) по пять игроков:
/// герой с уровнем, игрок, У/С/П, ценность, добивания, GPM/XPM, урон с полоской, урон по строениям и предметы.
/// </summary>
public partial class HistoryView
{
    private const double ScoreTableWidth = 932;

    private static readonly (string Key, double Width)[] ScoreColumns =
    {
        ("hero", 236), ("kda", 92), ("nw", 64), ("lhdn", 72), ("gpmxpm", 84), ("dmg", 96), ("bld", 60), ("items", 226),
    };

    internal static FrameworkElement Scoreboard(MatchReview r, Loc L)
    {
        var root = new StackPanel { Width = ScoreTableWidth };
        root.Children.Add(ScoreHeader(r, L));
        root.Children.Add(KillShareBar(r));
        int maxDamage = Math.Max(1, r.Players.Select(p => p.HeroDamage).DefaultIfEmpty(0).Max());
        root.Children.Add(TeamTable(r, radiant: true, maxDamage, L));
        root.Children.Add(TeamTable(r, radiant: false, maxDamage, L));
        Motion.FadeIn(root, 0, 350);
        return new Viewbox
        {
            Child = root, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10)
        };
    }

    // цвета сторон как в игре и на STRATZ — не зависят от темы ROSHIQ: Свет — зелёный, Тьма — красный
    private static readonly Brush RadiantBrush = Frozen(0x5C, 0xC9, 0x6B);
    private static readonly Brush DireBrush = Frozen(0xE8, 0x5A, 0x4F);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Brush TeamBrush(bool radiant) => radiant ? RadiantBrush : DireBrush;

    /// <summary>«Свет 32 — 38:12 — 21 Тьма», название победителя — под счётом.</summary>
    private static FrameworkElement ScoreHeader(MatchReview r, Loc L)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        FrameworkElement Side(bool radiant)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = radiant ? HorizontalAlignment.Right : HorizontalAlignment.Left };
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = radiant ? new Thickness(0, 0, 16, 0) : new Thickness(16, 0, 0, 0) };
            name.Children.Add(new TextBlock
            {
                Text = L.Get(radiant ? "score_radiant" : "score_dire"), FontSize = 15, FontWeight = FontWeights.SemiBold,
                Foreground = TeamBrush(radiant), HorizontalAlignment = radiant ? HorizontalAlignment.Right : HorizontalAlignment.Left
            });
            bool? won = r.RadiantWin is bool rw ? rw == radiant : null;
            name.Children.Add(new TextBlock
            {
                Text = won == true ? L.Get("score_victory") : won == false ? L.Get("score_defeat") : "",
                FontSize = 11.5, Foreground = won == true ? Ui.Res("Text") : Ui.Res("Faint"),
                HorizontalAlignment = radiant ? HorizontalAlignment.Right : HorizontalAlignment.Left
            });
            var score = new TextBlock
            {
                Text = (radiant ? r.RadiantScore : r.DireScore).ToString(CultureInfo.InvariantCulture),
                FontFamily = (FontFamily)Application.Current.FindResource("HeadlineFont"), FontSize = 40, FontWeight = FontWeights.Bold,
                Foreground = TeamBrush(radiant), VerticalAlignment = VerticalAlignment.Center
            };
            if (radiant) { panel.Children.Add(name); panel.Children.Add(score); }
            else { panel.Children.Add(score); panel.Children.Add(name); }
            return panel;
        }

        var left = Side(true);
        grid.Children.Add(left);
        var clock = new Border
        {
            Background = Ui.Res("Panel3"), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(22, 0, 22, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = r.Duration > 0 ? GameTimers.Clock(r.Duration) : "—", FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"),
                FontSize = 15, Foreground = Ui.Res("Text")
            }
        };
        Grid.SetColumn(clock, 1);
        grid.Children.Add(clock);
        var right = Side(false);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>Полоса доли убийств: слева Свет, справа Тьма.</summary>
    private static FrameworkElement KillShareBar(MatchReview r)
    {
        int total = r.RadiantScore + r.DireScore;
        var grid = new Grid { Height = 4, Margin = new Thickness(0, 0, 0, 12) };
        double share = total > 0 ? (double)r.RadiantScore / total : 0.5;
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.01, share), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.01, 1 - share), GridUnitType.Star) });
        grid.Children.Add(new Border { Background = TeamBrush(true), CornerRadius = new CornerRadius(2, 0, 0, 2), Margin = new Thickness(0, 0, 1, 0) });
        var dire = new Border { Background = TeamBrush(false), CornerRadius = new CornerRadius(0, 2, 2, 0), Margin = new Thickness(1, 0, 0, 0) };
        Grid.SetColumn(dire, 1);
        grid.Children.Add(dire);
        return grid;
    }

    private static Grid ScoreGrid()
    {
        var grid = new Grid();
        foreach (var (_, w) in ScoreColumns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        return grid;
    }

    private static string Thousands(int v) => v >= 1000
        ? (v / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k"
        : v.ToString(CultureInfo.InvariantCulture);

    private static FrameworkElement TeamTable(MatchReview r, bool radiant, int maxDamage, Loc L)
    {
        var rows = r.Players.Where(p => p.IsRadiant == radiant).ToList();
        var box = new Border
        {
            Background = Ui.Res("Panel2"), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 10),
            BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1), ClipToBounds = true
        };
        var stack = new StackPanel();
        box.Child = stack;

        // шапка команды: цветная полоса, название, суммарная ценность, подписи колонок
        var head = ScoreGrid();
        head.Margin = new Thickness(0, 0, 0, 0);
        var headBg = new Border { Background = Ui.Res("Panel3"), BorderBrush = TeamBrush(radiant), BorderThickness = new Thickness(3, 0, 0, 0) };
        Grid.SetColumnSpan(headBg, ScoreColumns.Length);
        head.Children.Add(headBg);
        int teamNw = rows.Sum(p => p.NetWorth);
        var title = new TextBlock
        {
            Text = L.Get(radiant ? "score_radiant" : "score_dire") + (r.RadiantWin is bool rw && rw == radiant ? "  ·  " + L.Get("score_victory") : ""),
            Foreground = TeamBrush(radiant), FontWeight = FontWeights.SemiBold, FontSize = 12.5,
            Margin = new Thickness(14, 7, 0, 7), VerticalAlignment = VerticalAlignment.Center
        };
        head.Children.Add(title);
        for (int c = 1; c < ScoreColumns.Length; c++)
        {
            var label = new TextBlock
            {
                Text = L.Get("score_col_" + ScoreColumns[c].Key), FontSize = 11, Foreground = Ui.Res("Faint"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0)
            };
            if (ScoreColumns[c].Key == "nw") label.Text += "\n" + Thousands(teamNw);
            Grid.SetColumn(label, c);
            head.Children.Add(label);
        }
        stack.Children.Add(head);

        foreach (var p in rows) stack.Children.Add(PlayerRow(p, maxDamage, L));
        return box;
    }

    private static FrameworkElement PlayerRow(ScoreRow p, int maxDamage, Loc L)
    {
        var grid = ScoreGrid();
        grid.MinHeight = 46;
        var bg = new Border
        {
            Background = p.IsYou ? Ui.Res("AccentSoft") : Brushes.Transparent,
            BorderBrush = p.IsYou ? Ui.Res("Accent") : Ui.Res("Line"),
            BorderThickness = p.IsYou ? new Thickness(3, 1, 0, 0) : new Thickness(0, 1, 0, 0)
        };
        Grid.SetColumnSpan(bg, ScoreColumns.Length);
        grid.Children.Add(bg);

        void Put(FrameworkElement e, int col)
        {
            e.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(e, col);
            grid.Children.Add(e);
        }

        // герой с уровнем + игрок
        var hero = App.Host.Database.FindHero(p.HeroId);
        var who = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 5, 0, 5) };
        var portrait = new Grid { Width = 64, Height = 36 };
        if (hero != null) portrait.Children.Add(Ui.Portrait(hero, 64));
        portrait.Children.Add(new Border
        {
            Background = Ui.Res("Bg"), CornerRadius = new CornerRadius(3), Padding = new Thickness(3, 0, 3, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 2, 3),
            Child = new TextBlock { Text = p.Level.ToString(CultureInfo.InvariantCulture), FontSize = 10, Foreground = Ui.Res("Text") }
        });
        who.Children.Add(portrait);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Width = 146 };
        names.Children.Add(new TextBlock
        {
            Text = hero?.DisplayName(L.Language) ?? "?", FontWeight = FontWeights.SemiBold, FontSize = 12.5,
            Foreground = Ui.Res("Text"), TextTrimming = TextTrimming.CharacterEllipsis
        });
        names.Children.Add(new TextBlock
        {
            Text = p.IsYou ? L.Get("score_you") : p.Name ?? L.Get("score_anon"), FontSize = 11,
            Foreground = p.IsYou ? Ui.Res("Accent") : Ui.Res("Faint"), TextTrimming = TextTrimming.CharacterEllipsis
        });
        who.Children.Add(names);
        Put(who, 0);

        // У/С/П: убийства — белые, смерти — красные, помощь — приглушённая
        var kda = new TextBlock { FontSize = 13, Margin = new Thickness(6, 0, 0, 0), FontFamily = (FontFamily)Application.Current.FindResource("MonoFont") };
        kda.Inlines.Add(new System.Windows.Documents.Run(p.Kills.ToString(CultureInfo.InvariantCulture)) { Foreground = Ui.Res("Text") });
        kda.Inlines.Add(new System.Windows.Documents.Run("/") { Foreground = Ui.Res("Faint") });
        kda.Inlines.Add(new System.Windows.Documents.Run(p.Deaths.ToString(CultureInfo.InvariantCulture)) { Foreground = Ui.Res("Red") });
        kda.Inlines.Add(new System.Windows.Documents.Run("/") { Foreground = Ui.Res("Faint") });
        kda.Inlines.Add(new System.Windows.Documents.Run(p.Assists.ToString(CultureInfo.InvariantCulture)) { Foreground = Ui.Res("Muted") });
        Put(kda, 1);

        Put(new TextBlock { Text = Thousands(p.NetWorth), Foreground = Ui.Res("Warning"), FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(6, 0, 0, 0) }, 2);
        Put(Pair(p.LastHits, p.Denies), 3);
        Put(Pair(p.Gpm, p.Xpm), 4);

        // урон по героям — число и полоска относительно лучшего в матче
        var dmg = new StackPanel { Margin = new Thickness(6, 0, 12, 0) };
        dmg.Children.Add(new TextBlock { Text = Thousands(p.HeroDamage), Foreground = Ui.Res("Text"), FontSize = 12.5 });
        var track = new Border { Height = 3, CornerRadius = new CornerRadius(2), Background = Ui.Res("Panel3"), Margin = new Thickness(0, 3, 0, 0) };
        var fill = new Border { Height = 3, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Background = TeamBrush(p.IsRadiant) };
        track.Child = fill;
        track.SizeChanged += (_, e) => fill.Width = Math.Max(2, e.NewSize.Width * p.HeroDamage / maxDamage);
        dmg.Children.Add(track);
        Put(dmg, 5);

        Put(new TextBlock { Text = Thousands(p.TowerDamage), Foreground = Ui.Res("Muted"), FontSize = 12.5, Margin = new Thickness(6, 0, 0, 0) }, 6);

        // предметы: шесть слотов и нейтральный — круглый
        var items = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
        foreach (var key in p.Items.Take(6))
        {
            var icon = key.Length > 0 ? Ui.ItemIcon(key, 26) : new Border { Width = 26, Height = 19, CornerRadius = new CornerRadius(3), Background = Ui.Res("Panel3") };
            icon.Margin = new Thickness(0, 0, 2, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            if (key.Length > 0) icon.ToolTip = App.Host.Database.FindItem(key)?.DisplayName(L.Language) ?? key;
            items.Children.Add(icon);
        }
        if (p.Neutral is string neutral && Roshiq.App.Services.HeroArt.ItemIcon(neutral) is { } img)
            items.Children.Add(new Border
            {
                Width = 21, Height = 21, CornerRadius = new CornerRadius(11), Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill }, BorderBrush = Ui.Res("LineStrong"), BorderThickness = new Thickness(1),
                ToolTip = App.Host.Database.FindItem(neutral)?.DisplayName(L.Language) ?? neutral
            });
        Put(items, 7);
        return grid;
    }

    /// <summary>Две цифры через «/»: первая — главная, вторая — приглушённая.</summary>
    private static TextBlock Pair(int a, int b)
    {
        var t = new TextBlock { FontSize = 12.5, Margin = new Thickness(6, 0, 0, 0) };
        t.Inlines.Add(new System.Windows.Documents.Run(a.ToString(CultureInfo.InvariantCulture)) { Foreground = Ui.Res("Text") });
        t.Inlines.Add(new System.Windows.Documents.Run(" / " + b.ToString(CultureInfo.InvariantCulture)) { Foreground = Ui.Res("Faint") });
        return t;
    }
}
