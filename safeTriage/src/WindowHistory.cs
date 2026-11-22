using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SafeTriage
{
    internal sealed partial class MainWindow
    {
        private readonly ReportHistory history = new ReportHistory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SafeTriage", "History"));
        private SavedReport[] savedReports = new SavedReport[0];
        private int skippedReports;
        private bool historyUnavailable;

        private void RefreshHistory()
        {
            historyUnavailable = false;
            try { savedReports = history.Read(out skippedReports); }
            catch (IOException) { historyUnavailable = true; }
            catch (UnauthorizedAccessException) { historyUnavailable = true; }
            if (historyUnavailable)
                savedReports = new SavedReport[0];
            FilterHistory();
        }

        private void FilterHistory()
        {
            string query = Find<TextBox>("HistoryFilter").Text.Trim();
            SavedReport[] matches = savedReports.Where(entry =>
                entry.Category.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (entry.Target ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            var list = Find<ListBox>("HistoryList");
            list.ItemsSource = matches;
            list.SelectedIndex = matches.Length > 0 ? 0 : -1;
            string status = historyUnavailable ? "暂时无法读取本地历史。" :
                savedReports.Length == 0 ? "还没有保存过报告。" :
                matches.Length == 0 ? "没有找到匹配的记录。" : matches.Length + " 条记录";
            if (!historyUnavailable && skippedReports > 0)
                status += " · " + skippedReports + " 条无法读取";
            Find<TextBlock>("HistoryStatus").Text = status;
        }

        private void SaveHistory(string category, string target, Report report, string content, Button button)
        {
            try
            {
                history.Save(new SavedReport { Category = category, Target = target, CreatedAt = report.CreatedAt, Content = content });
                button.Content = "已存入历史";
                button.IsEnabled = false;
            }
            catch (IOException)
            {
                MessageBox.Show(window, "暂时无法写入本地诊断历史。", "安诊", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (UnauthorizedAccessException)
            {
                MessageBox.Show(window, "当前账户无法写入本地诊断历史。", "安诊", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
