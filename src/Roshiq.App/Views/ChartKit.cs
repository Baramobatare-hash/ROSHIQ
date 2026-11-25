using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Roshiq.App.Views;

/// <summary>
/// Общие приёмы для графиков ROSHIQ: плавные кривые вместо ломаных, светящаяся линия, градиентная заливка,
/// «рисование» линии при первом показе, пульсирующая точка «сейчас» и подсказка с перекрестием при наведении.
/// Всё анимированное выключается вместе с «Уменьшать анимацию» (Motion.Enabled).
/// </summary>
public static class ChartKit
{
    public static Color ColorOf(string resource) =>
        Ui.Res(resource) is SolidColorBrush b ? b.Color : Colors.White;

    /// <summary>Плавная кривая через точки (сплайн Катмулла — Рома в виде кубических Безье, без «перелётов» за край).</summary>
    public static PathGeometry Smooth(IReadOnlyList<Point> p, double tension = 0.5)
    {
        var fig = new PathFigure { StartPoint = p[0], IsClosed = false };
        if (p.Count == 2) fig.Segments.Add(new LineSegment(p[1], true));
        for (int i = 0; i < p.Count - 1 && p.Count > 2; i++)
        {
            var p0 = p[Math.Max(0, i - 1)];
            var p1 = p[i];
            var p2 = p[i + 1];
            var p3 = p[Math.Min(p.Count - 1, i + 2)];
            double k = tension / 3;
            var c1 = new Point(p1.X + (p2.X - p0.X) * k, p1.Y + (p2.Y - p0.Y) * k);
            var c2 = new Point(p2.X - (p3.X - p1.X) * k, p2.Y - (p3.Y - p1.Y) * k);
            // контрольные точки не выходят за высоту отрезка — кривая не «прыгает» выше настоящих значений
            double lo = Math.Min(p1.Y, p2.Y), hi = Math.Max(p1.Y, p2.Y);
            c1.Y = Math.Clamp(c1.Y, lo, hi);
            c2.Y = Math.Clamp(c2.Y, lo, hi);
            fig.Segments.Add(new BezierSegment(c1, c2, p2, true));
        }
        var g = new PathGeometry();
        g.Figures.Add(fig);
        return g;
    }

    /// <summary>Светящаяся плавная линия.</summary>
    public static Path Curve(IReadOnlyList<Point> pts, string brush, double thickness, bool glow = true)
    {
        var path = new Path
        {
            Data = Smooth(pts), Stroke = Ui.Res(brush), StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };
        if (glow) path.Effect = new DropShadowEffect { Color = ColorOf(brush), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.75 };
        return path;
    }

    /// <summary>Градиентная заливка под кривой до линии baseY: насыщенно у линии, прозрачно внизу.</summary>
    public static Path Area(IReadOnlyList<Point> pts, double baseY, string brush, double topOpacity = 0.35)
    {
        var line = Smooth(pts);
        var fig = line.Figures[0].Clone();
        fig.Segments.Add(new LineSegment(new Point(pts[^1].X, baseY), false));
        fig.Segments.Add(new LineSegment(new Point(pts[0].X, baseY), false));
        fig.IsClosed = true;
        var g = new PathGeometry();
        g.Figures.Add(fig);
        var c = ColorOf(brush);
        return new Path
        {
            Data = g, IsHitTestVisible = false,
            Fill = new LinearGradientBrush(Color.FromArgb((byte)(255 * topOpacity), c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90),
        };
    }

    /// <summary>Лента между двумя плавными кривыми (вилка рейтинга).</summary>
    public static Path Ribbon(IReadOnlyList<Point> top, IReadOnlyList<Point> bottom, string brush, double opacity = 0.28)
    {
        var up = Smooth(top).Figures[0].Clone();
        var back = bottom.Reverse().ToList();
        var down = Smooth(back).Figures[0];
        up.Segments.Add(new LineSegment(back[0], true));
        foreach (var s in down.Segments) up.Segments.Add(s.Clone());
        up.IsClosed = true;
        var g = new PathGeometry();
        g.Figures.Add(up);
        var c = ColorOf(brush);
        return new Path
        {
            Data = g, IsHitTestVisible = false,
            Fill = new LinearGradientBrush(Color.FromArgb((byte)(255 * opacity), c.R, c.G, c.B), Color.FromArgb((byte)(255 * opacity * 0.35), c.R, c.G, c.B), 0),
        };
    }

    /// <summary>Линия «рисуется» слева направо; заливка проявляется следом.</summary>
    public static void DrawOn(Shape shape, int delay = 0, int ms = 1000)
    {
        if (!Motion.Enabled) return;
        double length = 0;
        if (shape is Path { Data: PathGeometry g })
        {
            var flat = g.GetFlattenedPathGeometry();
            foreach (var f in flat.Figures)
            {
                var prev = f.StartPoint;
                foreach (var s in f.Segments.OfType<PolyLineSegment>())
                    foreach (var pt in s.Points) { length += (pt - prev).Length; prev = pt; }
            }
        }
        if (length <= 0) return;
        double units = length / Math.Max(0.5, shape.StrokeThickness) + 2;
        shape.StrokeDashArray = new DoubleCollection { units, units };
        shape.StrokeDashOffset = units;
        var a = new DoubleAnimation(units, 0, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        a.Completed += (_, _) =>
        {
            shape.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            shape.StrokeDashArray = null;
            shape.StrokeDashOffset = 0;
        };
        shape.BeginAnimation(Shape.StrokeDashOffsetProperty, a);
    }

    public static void FadeIn(UIElement e, int delay, int ms)
    {
        if (!Motion.Enabled) return;
        e.Opacity = 0;
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(delay) });
    }

    /// <summary>Точка «сейчас»: ядро и расходящееся кольцо, которое мягко пульсирует.</summary>
    public static void PulseDot(Canvas c, Point p, string brush, double size = 8)
    {
        var color = ColorOf(brush);
        var ring = new Ellipse
        {
            Width = size * 3, Height = size * 3, Stroke = new SolidColorBrush(color), StrokeThickness = 1.5, IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.4, 0.4), Opacity = 0,
        };
        Canvas.SetLeft(ring, p.X - size * 1.5);
        Canvas.SetTop(ring, p.Y - size * 1.5);
        c.Children.Add(ring);
        var dot = new Ellipse
        {
            Width = size, Height = size, Fill = new SolidColorBrush(color), IsHitTestVisible = false,
            Effect = new DropShadowEffect { Color = color, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 },
        };
        Canvas.SetLeft(dot, p.X - size / 2);
        Canvas.SetTop(dot, p.Y - size / 2);
        c.Children.Add(dot);
        if (!Motion.Enabled) return;
        var period = TimeSpan.FromMilliseconds(1800);
        var grow = new DoubleAnimation(0.4, 1.0, period) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        var fade = new DoubleAnimation(0.8, 0, period) { RepeatBehavior = RepeatBehavior.Forever };
        ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        ring.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    public static TextBlock Label(Canvas c, string text, double x, double y, string brush = "Faint", double size = 10, FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, Foreground = Ui.Res(brush), FontSize = size, IsHitTestVisible = false, FontWeight = weight ?? FontWeights.Normal };
        Canvas.SetLeft(t, x);
        Canvas.SetTop(t, y);
        c.Children.Add(t);
        return t;
    }

    // ------------------------------------------------------------------ наведение

    /// <summary>Что показать при наведении: x перекрестия, текст подсказки и точки на кривых (y, цвет).</summary>
    public sealed record Probe(double X, string Text, IReadOnlyList<(double Y, string Brush)> Dots);

    private sealed class HoverState
    {
        public Func<double, Probe?>? Probe;
        public Line? Cross;
        public Border? Tip;
        public readonly List<Ellipse> Dots = new();
    }

    private static readonly ConditionalWeakTable<Canvas, HoverState> States = new();

    /// <summary>
    /// Подключить подсказку к холсту. Вызывать после каждой перерисовки: элементы подсказки добавляются заново
    /// поверх графика, обработчики мыши подписываются один раз.
    /// </summary>
    public static void Hover(Canvas c, Func<double, Probe?> probe)
    {
        bool fresh = !States.TryGetValue(c, out var st);
        st ??= new HoverState();
        if (fresh) States.Add(c, st);
        st.Probe = probe;
        if (c.Background == null) c.Background = Brushes.Transparent;
        st.Cross = new Line { Stroke = Ui.Res("Muted"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 }, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        c.Children.Add(st.Cross);
        st.Dots.Clear();
        st.Tip = new Border
        {
            Background = Ui.Res("Panel3"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5, 8, 5), Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.45, Color = Colors.Black },
            Child = new TextBlock { Foreground = Ui.Res("Text"), FontSize = 11.5 },
        };
        Panel.SetZIndex(st.Tip, 100);
        c.Children.Add(st.Tip);
        if (!fresh) return;
        c.MouseMove += (_, e) => Move(c, e.GetPosition(c));
        c.MouseLeave += (_, _) => Hide(c);
    }

    private static void Hide(Canvas c)
    {
        if (!States.TryGetValue(c, out var st)) return;
        if (st.Cross != null) st.Cross.Visibility = Visibility.Collapsed;
        if (st.Tip != null) st.Tip.Visibility = Visibility.Collapsed;
        foreach (var d in st.Dots) d.Visibility = Visibility.Collapsed;
    }

    private static void Move(Canvas c, Point at)
    {
        if (!States.TryGetValue(c, out var st) || st.Probe?.Invoke(at.X) is not { } p || st.Cross == null || st.Tip == null) { Hide(c); return; }
        double h = c.ActualHeight, w = c.ActualWidth;
        st.Cross.X1 = st.Cross.X2 = p.X;
        st.Cross.Y1 = 0;
        st.Cross.Y2 = h;
        st.Cross.Visibility = Visibility.Visible;
        while (st.Dots.Count < p.Dots.Count)
        {
            var d = new Ellipse { Width = 9, Height = 9, StrokeThickness = 2, Fill = Ui.Res("Panel"), IsHitTestVisible = false };
            Panel.SetZIndex(d, 90);
            st.Dots.Add(d);
            c.Children.Add(d);
        }
        for (int i = 0; i < st.Dots.Count; i++)
        {
            var d = st.Dots[i];
            if (i >= p.Dots.Count) { d.Visibility = Visibility.Collapsed; continue; }
            d.Stroke = Ui.Res(p.Dots[i].Brush);
            Canvas.SetLeft(d, p.X - 4.5);
            Canvas.SetTop(d, p.Dots[i].Y - 4.5);
            d.Visibility = Visibility.Visible;
        }
        ((TextBlock)st.Tip.Child).Text = p.Text;
        st.Tip.Visibility = Visibility.Visible;
        st.Tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = st.Tip.DesiredSize;
        double x = p.X + 12;
        if (x + size.Width > w) x = p.X - 12 - size.Width;
        double y = Math.Clamp((p.Dots.Count > 0 ? p.Dots.Min(d => d.Y) : h / 2) - size.Height - 8, 0, Math.Max(0, h - size.Height));
        Canvas.SetLeft(st.Tip, Math.Max(0, x));
        Canvas.SetTop(st.Tip, y);
    }

    /// <summary>Индекс ближайшей по горизонтали точки.</summary>
    public static int Nearest(IReadOnlyList<double> xs, double x)
    {
        int best = 0;
        for (int i = 1; i < xs.Count; i++) if (Math.Abs(xs[i] - x) < Math.Abs(xs[best] - x)) best = i;
        return best;
    }
}
