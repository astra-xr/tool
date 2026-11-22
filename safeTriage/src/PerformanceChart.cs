using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace SafeTriage
{
    internal sealed class PerformanceChart : FrameworkElement
    {
        private const double PlotLeft = 108;
        private const double LaneHeight = 64;
        private readonly PerformanceSeries series;
        private readonly Brush ink, muted, accent, border;
        private readonly double duration;

        public PerformanceChart(PerformanceSeries series, Brush ink, Brush muted, Brush accent, Brush border)
        {
            this.series = series;
            this.ink = ink;
            this.muted = muted;
            this.accent = accent;
            this.border = border;
            duration = Math.Max(1, Math.Max(series.Points.Last().Offset, series.Marks.LastOrDefault()));
            Height = 216;
            AutomationProperties.SetName(this, "卡顿曲线：系统 CPU、物理内存与磁盘读写速率");
        }

        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            double width = ActualWidth - PlotLeft - 22;
            if (width <= 0)
                return;
            var line = new Pen(border, 1);
            var marker = new Pen(accent, 1) { DashStyle = DashStyles.Dash };
            double maxDisk = Math.Max(1, Math.Ceiling(series.Points.Max(point => point.Disk ?? 0)));
            for (int tick = 0; tick <= 4; tick++)
            {
                double x = PlotLeft + width * tick / 4;
                drawing.DrawLine(line, new Point(x, 7), new Point(x, 181));
                string caption = (duration * tick / 4).ToString("0.#") + " 秒";
                var text = Label(caption, muted);
                drawing.DrawText(text, new Point(Math.Min(x - text.Width / 2, ActualWidth - text.Width), 190));
            }
            Plot(drawing, 0, "系统 CPU", "100%", width, 100, point => point.Cpu, line);
            Plot(drawing, 1, "物理内存", "100%", width, 100, point => point.Memory, line);
            Plot(drawing, 2, "磁盘读写", maxDisk.ToString("0") + " MB/s", width, maxDisk, point => point.Disk, line);
            foreach (double mark in series.Marks)
            {
                double x = PlotLeft + width * mark / duration;
                drawing.DrawLine(marker, new Point(x, 7), new Point(x, 181));
                drawing.DrawEllipse(accent, null, new Point(x, 6), 2.5, 2.5);
            }
        }

        private void Plot(DrawingContext drawing, int row, string title, string scale, double width, double maximum,
            Func<PerformancePoint, double?> read, Pen grid)
        {
            double top = 12 + LaneHeight * row;
            const double height = 40;
            drawing.DrawText(Label(title, ink), new Point(0, top));
            drawing.DrawText(Label("0 – " + scale, muted), new Point(0, top + 18));
            drawing.DrawLine(grid, new Point(PlotLeft, top + height), new Point(PlotLeft + width, top + height));
            Point? prior = null;
            bool hasReading = false;
            var trace = new Pen(accent, 1.7);
            foreach (PerformancePoint reading in series.Points)
            {
                double? value = read(reading);
                if (!value.HasValue)
                {
                    prior = null;
                    continue;
                }
                hasReading = true;
                var current = new Point(PlotLeft + width * reading.Offset / duration,
                    top + height * (1 - Math.Min(maximum, value.Value) / maximum));
                if (prior.HasValue)
                    drawing.DrawLine(trace, prior.Value, current);
                drawing.DrawEllipse(accent, null, current, 1.7, 1.7);
                prior = current;
            }
            if (!hasReading)
                drawing.DrawText(Label("未取得采样", muted), new Point(PlotLeft + 10, top + 8));
        }

        private FormattedText Label(string value, Brush color)
        {
            return new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), 11, color, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }
    }

    internal sealed partial class MainWindow
    {
        private Border BuildPerformanceChart(PerformanceSeries series)
        {
            var body = new StackPanel();
            body.Children.Add(Text("卡顿曲线", 17, ThemeBrush("Ink"), FontWeights.SemiBold));
            string caption = series.Marks.Length == 0 ? "横轴为录制时间，各项数据按自己的刻度显示。" :
                "竖向虚线为故障标记 · " + string.Join("、", series.Marks.Select(CaptureMarks.At));
            var hint = Text(caption, 12, ThemeBrush("Muted"), FontWeights.Normal);
            hint.Margin = new Thickness(0, 4, 0, 6);
            body.Children.Add(hint);
            body.Children.Add(new PerformanceChart(series, ThemeBrush("Ink"), ThemeBrush("Muted"),
                ThemeBrush("Accent"), ThemeBrush("AccentEdge")));
            var tile = Tile(body);
            tile.Margin = new Thickness(0, 8, 0, 0);
            return tile;
        }
    }
}
