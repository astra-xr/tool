using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CleanBox {
    public partial class MainForm {
        UiButton usageNavigation;
        SmoothGrid usageGrid;
        TextBox usageTarget;
        ChoiceButton usageDrive;
        Label usageSummary, usageLocation, usageDetail;
        FlowLayoutPanel usageBreadcrumbs;
        Button usageStart, usageFolder, usageUp, usageTop, usageOpen, usageTransfer, usagePrevious, usageNext;
        Button usageSaveSnapshot, usageCompareSnapshot;
        UsageResult usageResult;
        UsageNode usageCurrent;
        List<UsageNode> usageRows = new List<UsageNode>();
        bool usageLargest;
        int usagePage;
        const int UsagePageSize = 400;

        Panel BuildDiskUsage() {
            var page = Page();
            var heading = Heading("磁盘占用分析", "按文件长度统计，逐层查看目录与大文件");
            usageStart = Button("开始分析", async () => await AnalyzeUsage(), true);
            usageStart.Dock = DockStyle.Top; usageStart.AutoSize = false; usageStart.Height = 38;
            heading.Controls["headingActions"].Controls.Add(usageStart);
            var targetRow = new CanvasTablePanel { Dock = DockStyle.Top, Height = 44, ColumnCount = 3, RowCount = 1 };
            targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            targetRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            usageDrive = new ChoiceButton { Dock = DockStyle.Fill, AccessibleName = "选择分析磁盘" };
            usageDrive.Items.Add("选择磁盘");
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed)) usageDrive.Items.Add(drive.RootDirectory.FullName);
            usageDrive.SelectedIndex = 0;
            usageTarget = new TextBox { AccessibleName = "要分析的完整目录路径" };
            usageDrive.SelectedIndexChanged += (s, e) => { if (usageDrive.SelectedIndex > 0) usageTarget.Text = usageDrive.Items[usageDrive.SelectedIndex].ToString(); };
            usageFolder = Button("选择文件夹", () => {
                using (var dialog = new FolderBrowserDialog { Description = "选择要分析的本地文件夹", ShowNewFolderButton = false })
                    if (dialog.ShowDialog(this) == DialogResult.OK) usageTarget.Text = dialog.SelectedPath;
            });
            usageFolder.Dock = DockStyle.Fill; usageFolder.AutoSize = false; usageFolder.MinimumSize = Size.Empty;
            targetRow.Controls.Add(usageDrive, 0, 0); targetRow.Controls.Add(Input(usageTarget, new Padding(10,3,10,3)), 1, 0); targetRow.Controls.Add(usageFolder, 2, 0);
            usageSummary = TextLabel("选择磁盘或文件夹后开始分析。可取消并保留已扫描的结果。", 52, "muted");
            usageLocation = TextLabel("尚未分析", 28, "accent");
            usageBreadcrumbs = new CanvasFlowPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false,
                AccessibleName = "当前目录及其上级", Visible = false };
            var navigationBar = Bar(46);
            usageUp = Button("上一级", () => { if (usageCurrent != null && usageCurrent.Parent != null) ShowUsageDirectory(usageCurrent.Parent); });
            usageTop = Button("大文件 Top 200", () => { if (usageResult == null) return; usageLargest = true; usagePage = 0; RefreshUsageRows(); });
            var rootButton = Button("返回根目录", () => { if (usageResult != null) ShowUsageDirectory(usageResult.Root); });
            var analyzeCurrent = Button("分析此目录", async () => { if (usageCurrent != null && !working) { usageTarget.Text = usageCurrent.Path; await AnalyzeUsage(); } });
            usageSaveSnapshot = Button("保存为基线", async () => await SaveUsageSnapshot());
            usageCompareSnapshot = Button("与基线对比", async () => await CompareUsageSnapshot());
            navigationBar.Controls.AddRange(new Control[] { usageUp, rootButton, usageTop, analyzeCurrent, usageSaveSnapshot, usageCompareSnapshot });
            usageGrid = new SmoothGrid { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false, MultiSelect = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BorderStyle = BorderStyle.None, CellBorderStyle = DataGridViewCellBorderStyle.None, EnableHeadersVisualStyles = false,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None };
            usageGrid.Columns.Add("name", "名称 / 双击进入目录"); usageGrid.Columns.Add("size", "已统计大小 ↓"); usageGrid.Columns.Add("kind", "类型 / 状态");
            usageGrid.Columns.Add("share", "占比");
            usageGrid.FlatRows = true;
            usageGrid.Columns[0].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            foreach (DataGridViewColumn column in usageGrid.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            usageGrid.CellDoubleClick += (s, e) => { var item = SelectedUsage(); if (e.RowIndex >= 0 && item != null && item.Directory && !item.Link) ShowUsageDirectory(item); };
            usageGrid.SelectionChanged += (s, e) => UpdateUsageActions();
            usageGrid.CellPainting += PaintUsageShare;
            var surface = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(10) }; surface.Controls.Add(usageGrid);
            usageDetail = new Label { Dock = DockStyle.Bottom, Height = 64, AutoEllipsis = true, Tag = "muted", Text = "选择条目查看完整路径与统计范围。" };
            var actions = Bar(48); actions.Dock = DockStyle.Bottom;
            usageOpen = Button("打开位置", () => { var item = SelectedUsage(); if (item != null) OpenPath(item.Path); });
            usageTransfer = Button("转到文件解锁", TransferUsage, true);
            usagePrevious = Button("上一页", () => { if (usagePage > 0) { usagePage--; RefreshUsageRows(); } });
            usageNext = Button("下一页", () => { usagePage++; RefreshUsageRows(); });
            actions.Controls.AddRange(new Control[] { usageOpen, usageTransfer, usagePrevious, usageNext });
            page.Controls.Add(surface); page.Controls.Add(usageDetail); page.Controls.Add(actions);
            page.Controls.Add(usageLocation); page.Controls.Add(usageBreadcrumbs); page.Controls.Add(navigationBar); page.Controls.Add(usageSummary); page.Controls.Add(targetRow); page.Controls.Add(heading);
            UpdateUsageActions(); return page;
        }

        void StyleUsageGrid() {
            if (usageGrid == null) return;
            usageGrid.BackgroundColor = Palette.Surface;
            usageGrid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Palette.Surface, ForeColor = Ink, SelectionBackColor = Palette.Soft, SelectionForeColor = Ink, Padding = new Padding(Px(10),0,Px(6),0) };
            usageGrid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Palette.Side, ForeColor = Muted, SelectionBackColor = Palette.Side, SelectionForeColor = Muted, Padding = new Padding(Px(8),0,0,0) };
            usageGrid.Columns[1].Width = Px(122); usageGrid.Columns[2].Width = Px(108); usageGrid.Columns[3].Width = Px(116);
            usageGrid.ColumnHeadersHeight = Px(35); usageGrid.RowTemplate.Height = Px(44);
            usageGrid.RefreshMaterial();
        }
        UsageNode SelectedUsage() { return usageGrid == null || usageGrid.SelectedRows.Count == 0 ? null : usageGrid.SelectedRows[0].Tag as UsageNode; }
        void UpdateUsageActions() {
            if (usageStart == null || usageOpen == null) return;
            usageStart.Enabled = usageFolder.Enabled = usageDrive.Enabled = !working;
            usageTarget.ReadOnly = working;
            var item = SelectedUsage();
            usageOpen.Enabled = item != null && !working;
            usageTransfer.Enabled = item != null && !item.Link && !working;
            usageTop.Enabled = usageResult != null && !working;
            usageSaveSnapshot.Enabled = usageResult != null && !working && !usageResult.Canceled && !usageResult.Limited;
            usageCompareSnapshot.Enabled = usageResult != null && !working;
            usageUp.Enabled = usageCurrent != null && usageCurrent.Parent != null && !usageLargest;
            usagePrevious.Enabled = usagePage > 0; usageNext.Enabled = (usagePage + 1) * UsagePageSize < usageRows.Count;
            if (item != null) {
                usageDetail.Text = item.Path + "\r\n" + item.SizeText + (item.Partial ? " · 统计不完整，可能存在无权限文件或链接。" : " · 文件长度合计；实际可释放空间可能不同。");
                tips.SetToolTip(usageDetail, usageDetail.Text);
            }
        }
        void TransferUsage() {
            var item = SelectedUsage();
            if (working || item == null || item.Link) return;
            if (!File.Exists(item.Path) && !Directory.Exists(item.Path)) { latestStatus = "目标已不存在，请重新分析。"; return; }
            pathBox.Text = item.Path; SwitchPage(1);
        }
        void ShowUsageDirectory(UsageNode directory) { usageCurrent = directory; usageLargest = false; usagePage = 0; RefreshUsageRows(); }
        void RefreshUsageRows() {
            if (usageResult == null) return;
            usageRows = usageLargest ? usageResult.Largest : usageCurrent.Children.OrderByDescending(n => n.Bytes).ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            int pages = Math.Max(1, (usageRows.Count + UsagePageSize - 1) / UsagePageSize);
            usagePage = Math.Min(usagePage, pages - 1);
            usageGrid.Rows.Clear();
            foreach (var item in usageRows.Skip(usagePage * UsagePageSize).Take(UsagePageSize)) {
                int row = usageGrid.Rows.Add(item.Name, item.SizeText, item.Link ? "链接 · 已跳过" : (item.Directory ? "文件夹" : "文件") + (item.Partial ? " · 部分" : ""), UsageShareText(item));
                usageGrid.Rows[row].Tag = item; usageGrid.Rows[row].Cells[0].ToolTipText = item.Path;
                usageGrid.Rows[row].Cells[3].ToolTipText = usageLargest ? "占本次扫描范围内已统计大小的比例" : "占当前目录内已统计大小的比例";
            }
            usageGrid.ClearSelection(); usageGrid.CurrentCell = null;
            usageLocation.Text = (usageLargest ? "已扫描范围内最大的 200 个文件" : "当前目录") + " · " + usageRows.Count + " 项 · 第 " + (usagePage + 1) + "/" + pages + " 页";
            tips.SetToolTip(usageLocation, usageLargest ? usageResult.Root.Path : usageCurrent.Path);
            RefreshUsageBreadcrumbs();
            usageDetail.Text = usageRows.Count == 0 ? "此目录没有可显示的条目。统计不完整时可查看上方说明。" : "目录按已统计大小降序排列，双击文件夹进入。选择条目可转到文件解锁。";
            UpdateUsageActions();
        }

        void RefreshUsageBreadcrumbs() {
            usageBreadcrumbs.SuspendLayout();
            try {
                while (usageBreadcrumbs.Controls.Count > 0) usageBreadcrumbs.Controls[0].Dispose();
                var ancestors = new List<UsageNode>();
                for (var node = usageLargest ? usageResult.Root : usageCurrent; node != null; node = node.Parent)
                    ancestors.Add(node);
                ancestors.Reverse();
                for (int i = 0; i < ancestors.Count; i++) {
                    if (i > 0 && i < ancestors.Count - 2) {
                        if (i == 1) {
                            var omitted = TextLabel("› …", 27, "muted"); omitted.AutoSize = true; omitted.Dock = DockStyle.None;
                            omitted.Margin = new Padding(Px(3), Px(5), Px(3), 0);
                            usageBreadcrumbs.Controls.Add(omitted);
                        }
                        continue;
                    }
                    var node = ancestors[i];
                    string caption = node.Parent == null ? Path.GetFileName(node.Path.TrimEnd('\\')) : node.Name;
                    if (caption.Length == 0) caption = node.Path;
                    if (caption.Length > 16) caption = caption.Substring(0, 15) + "…";
                    var crumb = new UiButton { Name = "usageBreadcrumb", Text = (i == 0 ? "" : "›  ") + caption,
                        AccessibleName = node.Path, Kind = "secondary", Height = Px(28), AutoSize = true,
                        Margin = new Padding(0, 0, Px(4), 0), Enabled = usageLargest || node != usageCurrent };
                    crumb.Click += (s, e) => ShowUsageDirectory(node);
                    usageBreadcrumbs.Controls.Add(crumb);
                    tips.SetToolTip(crumb, node.Path);
                }
                usageBreadcrumbs.Visible = true;
            } finally { usageBreadcrumbs.ResumeLayout(true); }
        }

        double? UsageShare(UsageNode item) {
            long total = (usageLargest ? usageResult.Root : usageCurrent).Bytes;
            return total > 0 && !item.Link ? Math.Min(1.0, (double)item.Bytes / total) : (double?)null;
        }
        string UsageShareText(UsageNode item) {
            var share = UsageShare(item);
            return share.HasValue ? share.Value.ToString("P0") : "—";
        }
        void PaintUsageShare(object sender, DataGridViewCellPaintingEventArgs e) {
            if (e.RowIndex < 0 || e.ColumnIndex != 3) return;
            var item = usageGrid.Rows[e.RowIndex].Tag as UsageNode;
            if (item == null) return;
            var share = UsageShare(item);
            if (!share.HasValue) return;
            e.Paint(e.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.SelectionBackground | DataGridViewPaintParts.Border);
            var bounds = e.CellBounds; bounds.Inflate(-Px(8), 0);
            var bar = new Rectangle(bounds.Left, bounds.Bottom - Px(10), Math.Max(1, bounds.Width), Px(3));
            using (var track = new SolidBrush(Palette.Side)) e.Graphics.FillRectangle(track,bar);
            using (var fill = new SolidBrush(Teal)) e.Graphics.FillRectangle(fill,bar.X,bar.Y,(int)Math.Round(bar.Width*share.Value),bar.Height);
            bounds.Height -= Px(8);
            TextRenderer.DrawText(e.Graphics, Convert.ToString(e.FormattedValue), Font, bounds, Ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping);
            e.Handled = true;
        }
        async Task AnalyzeUsage() {
            if (!Idle()) return;
            string target = usageTarget.Text;
            cts = new System.Threading.CancellationTokenSource(); Busy(true, true);
            usageSummary.Text = "正在分析，可随时取消…"; latestStatus = "正在分析磁盘占用…";
            try {
                var result = await Task.Run(() => DiskUsage.Scan(target, cts.Token, s => latestStatus = s));
                usageResult = result; ShowUsageDirectory(result.Root);
                usageSummary.Text = result.Root.SizeText + " · " + result.Files + " 个文件 · 跳过或未能读取 " + result.Skipped + " 项\r\n"
                    + (result.Canceled ? "已取消，显示已扫描的部分。" : result.Limited ? "达到 30 万项上限，显示部分结果。可选择具体子目录继续分析。" : result.Root.Partial ? "部分内容无法统计，请结合目录状态查看。" : "分析完成。目录大小为文件长度合计。") ;
                latestStatus = result.Canceled ? "磁盘分析已取消，保留部分结果" : "磁盘占用分析完成";
            } catch (Exception ex) { usageSummary.Text = "本次分析未完成。"; ShowError(ex); }
            finally { Busy(false); cts.Dispose(); cts = null; UpdateUsageActions(); }
        }

        static string UsageSnapshotDirectory { get { return Path.Combine(Store.Data, "disk-snapshots"); } }

        async Task SaveUsageSnapshot() {
            if (working || usageResult == null) return;
            var result = usageResult;
            if (!Confirm("保存为基线", result.Root.Path + "\r\n\r\n本次扫描将替换此范围已有的基线。下次扫描同一路径后，可选择“与基线对比”。"
                + (result.Root.Partial ? "\r\n\r\n本次部分目录无法统计，对比时将标出无法确定的变化。" : ""), false)) return;
            Busy(true);
            try {
                await Task.Run(() => DiskSnapshot.Save(UsageSnapshotDirectory, result));
                latestStatus = "已保存磁盘基线：" + result.Root.Path;
                Store.Log(latestStatus);
            } catch (Exception ex) { ShowError(ex); }
            finally { Busy(false); }
        }

        async Task CompareUsageSnapshot() {
            if (working || usageResult == null) return;
            var current = usageResult;
            DiskSnapshot previous;
            Busy(true);
            try { previous = await Task.Run(() => DiskSnapshot.Load(UsageSnapshotDirectory, current.Root.Path)); }
            catch (Exception ex) { ShowError(ex); return; }
            finally { Busy(false); }
            ShowUsageComparison(previous, current);
        }

        void ShowUsageComparison(DiskSnapshot previous, UsageResult current) {
            using (var dialog = new Form { Text = "磁盘占用对比", Font = Font, ClientSize = new Size(850, 540),
                MinimumSize = new Size(680, 430), StartPosition = FormStartPosition.CenterParent, AutoScaleMode = AutoScaleMode.None, Padding = new Padding(16) }) {
                var scopes = new Stack<UsageChange>();
                var changes = new List<UsageChange>();
                int page = 0;
                var info = TextLabel("基线 " + previous.ScannedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                    + "    本次 " + current.ScannedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                    + "\r\n按增长量排序。双击目录继续查看。扫描范围不完整时标为“无法确定”。", 64, "muted");
                var location = TextLabel("", 35, "accent");
                location.AutoEllipsis = true;
                var grid = new SmoothGrid { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
                    AllowUserToDeleteRows = false, AllowUserToResizeRows = false, MultiSelect = false, RowHeadersVisible = false,
                    SelectionMode = DataGridViewSelectionMode.FullRowSelect, BorderStyle = BorderStyle.None, EnableHeadersVisualStyles = false,
                    BackgroundColor = Palette.Surface, DefaultCellStyle = new DataGridViewCellStyle { BackColor = Palette.Surface, ForeColor = Ink, SelectionBackColor = Palette.Soft, SelectionForeColor = Ink } };
                grid.Columns.Add("name", "名称"); grid.Columns.Add("baseline", "基线大小"); grid.Columns.Add("current", "本次大小"); grid.Columns.Add("change", "变化");
                grid.Columns[0].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                foreach (DataGridViewColumn column in grid.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
                var bar = Bar(48); bar.Dock = DockStyle.Bottom;
                Button up = null, back = null, next = null;
                Action render = () => {
                    grid.Rows.Clear();
                    foreach (var change in changes.Skip(page * UsagePageSize).Take(UsagePageSize)) {
                        var row = grid.Rows[grid.Rows.Add(change.Item.Name + (change.HasDirectory ? "\\" : ""), change.BeforeText, change.AfterText, change.ChangeText)];
                        row.Tag = change; row.Cells[0].ToolTipText = change.Item.Path;
                    }
                    grid.ClearSelection();
                    int pages = Math.Max(1, (changes.Count + UsagePageSize - 1) / UsagePageSize);
                    var scope = scopes.Peek();
                    location.Text = scope.Item.Path + " · " + scope.ChangeText + " · 第 " + (page + 1) + "/" + pages + " 页";
                    up.Enabled = scopes.Count > 1;
                    back.Enabled = page > 0;
                    next.Enabled = page + 1 < pages;
                };
                Action refresh = () => { changes = scopes.Peek().Children(); page = 0; render(); };
                up = Button("上一级", () => { if (scopes.Count > 1) { scopes.Pop(); refresh(); } });
                back = Button("上一页", () => { if (page > 0) { page--; render(); } });
                next = Button("下一页", () => { page++; render(); });
                bar.Controls.AddRange(new Control[] { up, back, next });
                grid.CellDoubleClick += (s, e) => {
                    if (e.RowIndex < 0) return;
                    var change = grid.Rows[e.RowIndex].Tag as UsageChange;
                    if (change != null && change.HasDirectory) { scopes.Push(change); refresh(); }
                };
                dialog.Controls.Add(grid); dialog.Controls.Add(bar); dialog.Controls.Add(location); dialog.Controls.Add(info);
                ApplyTheme(dialog);
                if (dpiScale != 1) dialog.Scale(new SizeF(dpiScale, dpiScale));
                scopes.Push(new UsageChange(previous.Root, current.Root));
                refresh();
                dialog.ShowDialog(this);
            }
        }
    }
}
