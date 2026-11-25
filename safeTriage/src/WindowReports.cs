using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SafeTriage
{
    internal sealed partial class MainWindow
    {
        private readonly Dictionary<Tuple<string, string>, Report> snapshots = new Dictionary<Tuple<string, string>, Report>();

        private void ShowResults(StackPanel output, string category, string scope, string subtitle, Report report, bool recordSnapshot = true)
        {
            var key = Tuple.Create(category, scope);
            recordSnapshot = recordSnapshot && report.CanCompare;
            Report previous = null;
            if (recordSnapshot)
                snapshots.TryGetValue(key, out previous);
            FindingChange[] changes = previous != null ? ReportOutput.Compare(previous, report) : new FindingChange[0];

            output.Children.Clear();
            output.Visibility = Visibility.Visible;

            var heading = new StackPanel();
            var row = new DockPanel();
            string title = category + "结果" + (string.IsNullOrEmpty(subtitle) ? "" : " · " + subtitle);
            var export = new Button
            {
                Style = (Style)window.Resources["SecondaryButton"], Content = "导出脱敏报告",
                Padding = new Thickness(11, 5, 11, 5), Margin = new Thickness(10, 0, 0, 0)
            };
            export.Click += (sender, args) => PreviewExport(ReportOutput.Text(category, report, previous, changes));
            DockPanel.SetDock(export, Dock.Right);
            row.Children.Add(export);
            if (recordSnapshot)
            {
                var saveHistory = new Button
                {
                    Style = (Style)window.Resources["SecondaryButton"], Content = "存入历史",
                    Padding = new Thickness(11, 5, 11, 5), Margin = new Thickness(10, 0, 0, 0)
                };
                saveHistory.Click += (sender, args) => SaveHistory(category, subtitle, report,
                    ReportOutput.Text(category, report, previous, changes), saveHistory);
                DockPanel.SetDock(saveHistory, Dock.Right);
                row.Children.Add(saveHistory);
            }
            var headingText = Text(title, 18, ThemeBrush("Ink"), FontWeights.SemiBold);
            headingText.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(headingText);
            heading.Children.Add(row);
            var summary = Text(report.Summary, 14, ThemeBrush("Muted"), FontWeights.Normal);
            summary.Margin = new Thickness(0, 7, 0, 0);
            heading.Children.Add(summary);
            var header = new Border { Style = (Style)window.Resources["SummaryTile"], Child = heading };
            output.Children.Add(header);

            if (previous != null)
                output.Children.Add(BuildComparison(previous, report, changes));
            if (report.Performance != null)
                output.Children.Add(BuildPerformanceChart(report.Performance));
            for (int i = 0; i < report.Findings.Length; i++)
            {
                bool? expanded = null;
                if (category == "故障时间线")
                    expanded = i < 3;
                else if (category == "卡顿现场" || category == "声音排查" || category == "启动排查")
                    expanded = true;
                else if (report.Findings[i].Name.StartsWith("故障标记", StringComparison.Ordinal))
                    expanded = true;
                output.Children.Add(BuildFinding(report.Findings[i], expanded));
            }
            if (recordSnapshot)
                snapshots[key] = report;

            if (output.IsVisible)
                header.BringIntoView();
        }

        private Border BuildComparison(Report before, Report after, FindingChange[] changes)
        {
            int states = changes.Count(change => change.StateChanged);
            int uncertain = changes.Count(change => change.Unconfirmed);
            string label = changes.Length == 0 ? "复查对比 · 未发现变化" :
                string.Format("复查对比 · {0} 项变化（{1} 项状态变化）", changes.Length - uncertain, states);
            if (uncertain > 0)
                label += " · " + uncertain + " 项未能核对";
            var header = Text(label, 14, ThemeBrush("Accent"), FontWeights.SemiBold);
            var contents = new StackPanel();
            var times = Text(string.Format("上次 {0:MM-dd HH:mm:ss} · 本次 {1:MM-dd HH:mm:ss}", before.CreatedAt, after.CreatedAt),
                12, ThemeBrush("Muted"), FontWeights.Normal);
            times.Margin = new Thickness(0, 5, 0, 8);
            contents.Children.Add(times);
            foreach (FindingChange change in changes)
            {
                var item = Text(change.Name, 13, ThemeBrush("Ink"), FontWeights.SemiBold);
                item.Margin = new Thickness(0, 6, 0, 3);
                contents.Children.Add(item);
                var columns = new Grid();
                columns.ColumnDefinitions.Add(new ColumnDefinition());
                columns.ColumnDefinitions.Add(new ColumnDefinition());
                var previous = ComparisonSide("上次", change.Before, change.Unconfirmed);
                previous.Margin = new Thickness(0, 0, 5, 0);
                columns.Children.Add(previous);
                var current = ComparisonSide("本次", change.After, change.Unconfirmed);
                current.Margin = new Thickness(5, 0, 0, 0);
                Grid.SetColumn(current, 1);
                columns.Children.Add(current);
                contents.Children.Add(columns);
            }

            var expander = new Expander
            {
                Header = header, Content = contents, Style = (Style)window.Resources["FindingExpander"],
                IsExpanded = states > 0 || uncertain > 0
            };
            var tile = Tile(expander);
            tile.Margin = new Thickness(0, 8, 0, 0);
            tile.Padding = new Thickness(9, 7, 9, 7);
            return tile;
        }

        private Border ComparisonSide(string label, Finding finding, bool unconfirmed)
        {
            var body = new StackPanel();
            string status = finding != null ? Finding.Status(finding.State) : unconfirmed ? "未能核对（读取不完整）" : "未出现";
            body.Children.Add(Text(label + " · " + status,
                12, ThemeBrush("Accent"), FontWeights.SemiBold));
            if (finding != null)
            {
                var detail = Text(finding.Detail, 12, ThemeBrush("Muted"), FontWeights.Normal);
                detail.Margin = new Thickness(0, 4, 0, 0);
                body.Children.Add(detail);
            }
            return new Border
            {
                CornerRadius = new CornerRadius(8), Background = ThemeBrush("SoftSurface"),
                Padding = new Thickness(9, 7, 9, 7), Child = body
            };
        }

        private void PreviewExport(string report)
        {
            var dialog = new Window
            {
                Title = "脱敏报告预览", Width = 645, Height = 560, MinWidth = 420, MinHeight = 350,
                Owner = window, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = window.Background, Foreground = ThemeBrush("Ink"), FontFamily = window.FontFamily
            };
            dialog.SourceInitialized += SetTitleBar;
            var root = new DockPanel { Margin = new Thickness(20) };
            var note = Text("此预览已省略域名、IP 地址、文件路径及进程和设备名称。请核对内容后保存。",
                13, ThemeBrush("Muted"), FontWeights.Normal);
            note.Margin = new Thickness(0, 0, 0, 11);
            DockPanel.SetDock(note, Dock.Top);
            root.Children.Add(note);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Margin = new Thickness(0, 12, 0, 0);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            var dismiss = new Button { Content = "关闭", Style = (Style)window.Resources["SecondaryButton"], IsCancel = true };
            var save = new Button { Content = "保存报告", Style = (Style)window.Resources["PrimaryButton"], Margin = new Thickness(8, 0, 0, 0) };
            buttons.Children.Add(dismiss);
            buttons.Children.Add(save);
            save.Click += (sender, args) =>
            {
                var picker = new SaveFileDialog
                {
                    Title = "保存脱敏报告", Filter = "文本报告 (*.txt)|*.txt", DefaultExt = ".txt",
                    FileName = "safeTriage-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt", OverwritePrompt = true
                };
                if (picker.ShowDialog(dialog) != true)
                    return;
                try
                {
                    ReportOutput.Save(picker.FileName, report);
                    dialog.DialogResult = true;
                }
                catch (Exception error)
                {
                    if (!(error is IOException) && !(error is UnauthorizedAccessException) &&
                        !(error is ArgumentException) && !(error is NotSupportedException) && !(error is System.Security.SecurityException))
                        throw;
                    MessageBox.Show(dialog, "暂时无法保存到这个位置。请换一个可写的文件夹。", "安诊", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            };
            var preview = new TextBox
            {
                IsReadOnly = true, Text = report, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = ThemeBrush("Surface"), Foreground = ThemeBrush("Ink"), BorderBrush = ThemeBrush("FieldEdge"), Padding = new Thickness(10),
                FontSize = 13
            };
            root.Children.Add(preview);
            dialog.Content = root;
            dialog.ShowDialog();
        }
    }
}
