using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CleanBox {
    public partial class MainForm {
        const int CleanupPreviewPageSize = 200;
        const int CleanupPreviewLargestCount = 12;

        Panel BuildCleanupContents(Form dialog, CleanupPlan plan) {
            var panel = new CanvasPanel { Dock = DockStyle.Bottom, Height = 38 };
            var body = new CanvasTablePanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Visible = false };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var state = TextLabel("处理范围", 25, "muted");
            var tree = new TreeView { Name = "cleanupPreviewTree", Dock = DockStyle.Fill, HideSelection = false,
                AccessibleName = "待处理的文件与文件夹", BorderStyle = BorderStyle.None, ShowNodeToolTips = true };
            var largest = new ListBox { Name = "cleanupPreviewLargest", Dock = DockStyle.Fill, HorizontalScrollbar = true,
                AccessibleName = "待处理范围内最大的文件", BorderStyle = BorderStyle.None, IntegralHeight = false };
            body.Controls.Add(state, 0, 0); body.Controls.Add(TextLabel("最大的文件", 25, "muted"), 1, 0);
            body.Controls.Add(tree, 0, 1); body.Controls.Add(largest, 1, 1);
            tree.BeforeExpand += (s, e) => {
                var load = e.Node.Tag as Action<TreeNode>;
                if (load == null) return;
                tree.BeginUpdate();
                try { e.Node.Tag = null; e.Node.Nodes.Clear(); load(e.Node); }
                finally { tree.EndUpdate(); }
            };
            var hint = new ToolTip();
            largest.SelectedIndexChanged += (s, e) => {
                var selected = largest.SelectedItem as CleanupPreviewFile;
                hint.SetToolTip(largest, selected == null ? "" : selected.Path);
            };
            dialog.Disposed += (s, e) => hint.Dispose();

            bool expanded = false, loaded = false, loading = false;
            int baseHeight = 0;
            Button toggle = null;
            toggle = Button("展开清理预览  ▾", async () => {
                if (baseHeight == 0) baseHeight = dialog.Height;
                expanded = !expanded;
                var screen = Screen.FromControl(dialog).WorkingArea;
                int extra = Math.Min(Px(250), Math.Max(Px(55), screen.Height - baseHeight + Px(45)));
                panel.Height = Px(38) + (expanded ? extra : 0);
                body.Visible = expanded;
                dialog.Height = baseHeight + (expanded ? Math.Max(0, extra - Px(55)) : 0);
                dialog.Top = Math.Max(screen.Top, Math.Min(dialog.Top, screen.Bottom - dialog.Height));
                toggle.Text = expanded ? "收起清理预览  ▴" : "展开清理预览  ▾";
                if (!expanded || loaded || loading) return;
                loading = true; state.Text = "正在准备清单…";
                try {
                    var data = await Task.Run(() => {
                        var folders = plan.Items.Where(item => item.Path != plan.Root)
                            .GroupBy(item => Path.GetDirectoryName(item.Path), StringComparer.Ordinal)
                            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Directory)
                                .ThenBy(item => Path.GetFileName(item.Path), StringComparer.CurrentCultureIgnoreCase).ToArray(), StringComparer.Ordinal);
                        var files = plan.Items.Where(item => !item.Directory).OrderByDescending(item => item.Bytes)
                            .Take(CleanupPreviewLargestCount).ToArray();
                        return Tuple.Create(folders, files);
                    });
                    if (dialog.IsDisposed) return;
                    var root = MakeCleanupNode(plan.Items[plan.Items.Count - 1], data.Item1);
                    tree.Nodes.Add(root); root.Expand();
                    foreach (var item in data.Item2) largest.Items.Add(new CleanupPreviewFile(item, plan.Root));
                    if (largest.Items.Count == 0) largest.Items.Add("此范围内没有文件");
                    state.Text = "处理范围"; loaded = true;
                } catch (Exception ex) {
                    if (!dialog.IsDisposed) { state.Text = "预览明细未能显示"; Store.Log("清理预览明细未能显示：" + ex.Message); }
                } finally { loading = false; }
            });
            toggle.Name = "cleanupPreviewToggle"; toggle.Dock = DockStyle.Top; toggle.AutoSize = false;
            toggle.Height = 34;
            panel.Controls.Add(body); panel.Controls.Add(toggle);
            return panel;
        }

        static TreeNode MakeCleanupNode(CleanupItem item, Dictionary<string,CleanupItem[]> folders) {
            string name = Path.GetFileName(item.Path);
            var node = new TreeNode(item.Directory ? name : name + "  ·  " + FileSizes.Format(item.Bytes)) { ToolTipText = item.Path };
            CleanupItem[] children;
            if (item.Directory && folders.TryGetValue(item.Path, out children))
                SetCleanupNodeLoader(node, parent => AddCleanupNodes(parent, children, 0, folders));
            return node;
        }

        static void SetCleanupNodeLoader(TreeNode node, Action<TreeNode> load) {
            node.Tag = load; node.Nodes.Add(new TreeNode());
        }

        static void AddCleanupNodes(TreeNode parent, CleanupItem[] children, int offset, Dictionary<string,CleanupItem[]> folders) {
            int end = Math.Min(children.Length, offset + CleanupPreviewPageSize);
            for (int i = offset; i < end; i++) parent.Nodes.Add(MakeCleanupNode(children[i], folders));
            if (end < children.Length) {
                var more = new TreeNode("继续查看 · 剩余 " + (children.Length - end) + " 项");
                SetCleanupNodeLoader(more, next => AddCleanupNodes(next, children, end, folders));
                parent.Nodes.Add(more);
            }
        }

        sealed class CleanupPreviewFile {
            internal readonly string Path;
            readonly string display;
            internal CleanupPreviewFile(CleanupItem item, string root) {
                Path = item.Path;
                display = FileSizes.Format(item.Bytes) + "  ·  " + (item.Path == root
                    ? System.IO.Path.GetFileName(root) : item.Path.Substring(root.Length + 1));
            }
            public override string ToString() { return display; }
        }
    }
}
