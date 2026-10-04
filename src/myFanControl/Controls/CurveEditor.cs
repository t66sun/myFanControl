using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FanControl.Core;
namespace myFanControl.Controls;
public sealed class CurveEditor : FrameworkElement
{
    public IReadOnlyList<CurvePoint> Fan1 { get; set; } = [];
    public IReadOnlyList<CurvePoint> Fan2 { get; set; } = [];
    public int SelectedFan { get; set; }
    public int SelectedNode { get; set; } = -1;
    public event Action<int, double, int>? NodeDragged;
    private int _drag = -1;
    private Rect Plot => new(58, 14, Math.Max(1, ActualWidth - 80), Math.Max(1, ActualHeight - 49));
    private double MinTemperature => Math.Min(20, Fan1.Concat(Fan2).Select(p => p.Celsius).DefaultIfEmpty(20).Min() - 5);
    private Point Map(CurvePoint p) => new(Plot.Left + (p.Celsius - MinTemperature) / (100 - MinTemperature) * Plot.Width, Plot.Bottom - (p.Rpm - 1500) / 6000d * Plot.Height);
    public CurveEditor() { Focusable = true; ToolTip = "拖拽节点：1°C / 100 RPM。方向键调整所选节点。"; }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(233, 238, 244)), 1);
        for (int rpm = 1500; rpm <= 7500; rpm += 1500) { double y = Map(new(20, rpm)).Y; dc.DrawLine(grid, new(Plot.Left, y), new(Plot.Right, y)); Label(dc, rpm.ToString(), 4, y - 8); }
        foreach (int t in new[] { 20, 40, 60, 80, 100 }) { if (t < MinTemperature) continue; double x = Map(new(t, 1500)).X; dc.DrawLine(grid, new(x, Plot.Top), new(x, Plot.Bottom)); Label(dc, t + "°", x - 10, Plot.Bottom + 8); }
        if (SelectedFan == 0) { DrawCurve(dc, Fan2, Color.FromRgb(166, 103, 32), false); DrawCurve(dc, Fan1, Color.FromRgb(36, 95, 190), true); } else { DrawCurve(dc, Fan1, Color.FromRgb(36, 95, 190), false); DrawCurve(dc, Fan2, Color.FromRgb(166, 103, 32), true); }
        if (IsKeyboardFocused) dc.DrawRectangle(null, new Pen(Brushes.SteelBlue, 1), new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)));
    }
    private void Label(DrawingContext dc, string text, double x, double y) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, new SolidColorBrush(Color.FromRgb(99, 115, 132)), VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
    private void DrawCurve(DrawingContext dc, IReadOnlyList<CurvePoint> points, Color color, bool selected)
    {
        if (points.Count == 0) return;
        var brush = new SolidColorBrush(color); var pen = new Pen(brush, selected ? 2.5 : 1.5) { LineJoin = PenLineJoin.Round };
        if (selected)
        {
            var area = new StreamGeometry();
            using (var context = area.Open())
            {
                context.BeginFigure(new Point(Plot.Left, Plot.Bottom), true, true);
                context.LineTo(new Point(Plot.Left, Map(points[0]).Y), true, false);
                foreach (var point in points) context.LineTo(Map(point), true, false);
                context.LineTo(new Point(Plot.Right, Map(points[^1]).Y), true, false);
                context.LineTo(new Point(Plot.Right, Plot.Bottom), true, false);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(12, color.R, color.G, color.B)), null, area);
        }
        Point previous = new(Plot.Left, Map(points[0]).Y);
        foreach (var p in points) { var point = Map(p); dc.DrawLine(pen, previous, point); previous = point; }
        dc.DrawLine(pen, previous, new(Plot.Right, previous.Y));
        for (int i = 0; i < points.Count; i++)
        {
            if (i == SelectedNode && selected) dc.DrawEllipse(null, new Pen(brush, 1), Map(points[i]), 9, 9);
            dc.DrawEllipse(Brushes.White, new Pen(brush, selected ? 2.5 : 1.5), Map(points[i]), selected ? 4.5 : 3.5, selected ? 4.5 : 3.5);
        }
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); Focus(); var points = SelectedFan == 0 ? Fan1 : Fan2; var mouse = e.GetPosition(this);
        _drag = Enumerable.Range(0, points.Count).FirstOrDefault(i => (Map(points[i]) - mouse).Length < 12, -1);
        if (_drag < 0) return; SelectedNode = _drag; CaptureMouse(); InvalidateVisual(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); if (_drag < 0 || !IsMouseCaptured) return;
        var p = e.GetPosition(this); MoveNode(_drag, Math.Round(MinTemperature + (p.X - Plot.Left) / Plot.Width * (100 - MinTemperature)), (int)Math.Round((1500 + (Plot.Bottom - p.Y) / Plot.Height * 6000) / 100) * 100);
    }
    private void MoveNode(int index, double t, int rpm)
    {
        var points = SelectedFan == 0 ? Fan1 : Fan2; if (index < 0 || index >= points.Count) return;
        double min = index == 0 ? -100 : Math.Floor(points[index - 1].Celsius) + 1;
        double max = index == points.Count - 1 ? 90 : Math.Ceiling(points[index + 1].Celsius) - 1;
        if (min > max) return;
        int low = index == 0 ? 1500 : points[index - 1].Rpm, high = index == points.Count - 1 ? 7500 : points[index + 1].Rpm;
        NodeDragged?.Invoke(index, Math.Clamp(t, min, max), index == points.Count - 1 ? 7500 : Math.Clamp(rpm, low, high));
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { _drag = -1; ReleaseMouseCapture(); base.OnMouseLeftButtonUp(e); }
    protected override void OnLostMouseCapture(MouseEventArgs e) { _drag = -1; base.OnLostMouseCapture(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var points = SelectedFan == 0 ? Fan1 : Fan2; if (points.Count == 0) return;
        SelectedNode = Math.Clamp(SelectedNode, 0, points.Count - 1);
        if (e.Key == Key.Tab) { base.OnKeyDown(e); return; }
        if (e.Key == Key.Home) SelectedNode = 0; else if (e.Key == Key.End) SelectedNode = points.Count - 1;
        else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) { var p = points[SelectedNode]; MoveNode(SelectedNode, p.Celsius + (e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0), p.Rpm + (e.Key == Key.Down ? -100 : e.Key == Key.Up ? 100 : 0)); }
        else { base.OnKeyDown(e); return; } e.Handled = true; InvalidateVisual();
    }
}


