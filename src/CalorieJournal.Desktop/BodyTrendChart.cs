using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CalorieJournal.Core;

namespace CalorieJournal.Desktop;

public sealed record BodyTrendPoint(DateOnly Date, decimal? Value);

public sealed class BodyTrendSeries
{
    public BodyTrendPoint[] Points { get; }
    public int Count => Points.Count(x => x.Value is not null);
    public decimal Minimum { get; }
    public decimal Maximum { get; }
    public string Unit { get; }
    public BodyTrendSeries(IEnumerable<BodyMeasurement> records, bool waist)
    {
        Unit = waist ? "cm" : "kg";
        Points = records.OrderBy(x => x.Date).Select(x => new BodyTrendPoint(x.Date, waist ? x.WaistCm : x.WeightKg)).ToArray();
        var values = Points.Where(x => x.Value is not null).Select(x => x.Value!.Value).ToArray();
        if (values.Length == 0) { Minimum = 0; Maximum = 1; return; }
        var padding = Math.Max(1m, (values.Max() - values.Min()) * .15m);
        Minimum = Math.Max(0, values.Min() - padding); Maximum = values.Max() + padding;
    }
    public Point Position(BodyTrendPoint point, Rect area)
    {
        if (point.Value is not { } value) throw new InvalidOperationException("缺失测量没有绘图坐标。");
        var span = Points[^1].Date.DayNumber - Points[0].Date.DayNumber;
        var x = span == 0 ? .5 : (point.Date.DayNumber - Points[0].Date.DayNumber) / (double)span;
        return new Point(area.Left + x * area.Width, area.Bottom - (double)((value - Minimum) / (Maximum - Minimum)) * area.Height);
    }
}

public sealed class BodyTrendChart : FrameworkElement
{
    public static readonly DependencyProperty RecordsProperty = DependencyProperty.Register(nameof(Records), typeof(IEnumerable<BodyMeasurement>), typeof(BodyTrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, RecordsChanged));
    public static readonly DependencyProperty WaistProperty = DependencyProperty.Register(nameof(Waist), typeof(bool), typeof(BodyTrendChart), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (sender, _) => ((BodyTrendChart)sender).series = null));
    public IEnumerable<BodyMeasurement>? Records { get => (IEnumerable<BodyMeasurement>?)GetValue(RecordsProperty); set => SetValue(RecordsProperty, value); }
    public bool Waist { get => (bool)GetValue(WaistProperty); set => SetValue(WaistProperty, value); }
    private BodyTrendSeries? series;
    public BodyTrendSeries Series => series ??= new BodyTrendSeries(Records ?? [], Waist);
    private Rect PlotArea => new(62, 32, Math.Max(1, ActualWidth - 82), Math.Max(1, ActualHeight - 76));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(43, 100, 77));
    private static void RecordsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var chart = (BodyTrendChart)sender;
        if (e.OldValue is INotifyCollectionChanged old) CollectionChangedEventManager.RemoveHandler(old, chart.CollectionChanged);
        if (e.NewValue is INotifyCollectionChanged current) CollectionChangedEventManager.AddHandler(current, chart.CollectionChanged);
        chart.series = null;
    }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) { series = null; InvalidateVisual(); }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing); drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var value = Series; var area = PlotArea;
        Label($"{(Waist ? "腰围" : "体重")} · {value.Unit} · {value.Count}次实际测量", 0, 0, Accent, 14);
        if (value.Count == 0) { Label(Waist && value.Points.Length > 0 ? "当前区间未填写腰围" : "当前区间未测量", 62, 66, Brushes.DimGray); return; }
        var axis = new Pen(Brushes.LightGray, 1);
        for (var index = 0; index < 3; index++)
        {
            var y = area.Top + area.Height * index / 2;
            drawing.DrawLine(axis, new Point(area.Left, y), new Point(area.Right, y));
            Label((value.Maximum - (value.Maximum - value.Minimum) * index / 2).ToString("0.##", CultureInfo.InvariantCulture), 1, y - 8, Brushes.DimGray);
        }
        Point? previous = null;
        foreach (var point in value.Points)
        {
            if (point.Value is null) { previous = null; continue; }
            var position = value.Position(point, area);
            if (previous is { } start) drawing.DrawLine(new Pen(Accent, 1.5), start, position);
            drawing.DrawEllipse(Accent, new Pen(Brushes.White, 1), position, 4, 4); previous = position;
        }
        var first = value.Points[0].Date.ToString("yyyy-MM-dd"); var last = value.Points[^1].Date.ToString("yyyy-MM-dd");
        Label(first, area.Left - 12, area.Bottom + 10, Brushes.DimGray);
        if (first != last) Label(last, area.Right - 80, area.Bottom + 10, Brushes.DimGray);
        void Label(string text, double x, double y, Brush brush, double size = 12) => drawing.DrawText(new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); var mouse = e.GetPosition(this); var value = Series; var area = PlotArea;
        var nearest = value.Points.Where(x => x.Value is not null).Select(x => new { Point = x, Distance = (value.Position(x, area) - mouse).Length }).MinBy(x => x.Distance);
        ToolTip = nearest is { Distance: <= 16 } ? $"{nearest.Point.Date:yyyy-MM-dd} · {nearest.Point.Value:0.##} {value.Unit}" : null;
    }
}
