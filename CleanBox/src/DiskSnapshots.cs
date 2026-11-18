using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CleanBox {
    internal sealed class DiskSnapshot {
        const int FormatVersion = 1;
        const int MaxEntries = 300000;
        internal UsageNode Root;
        internal DateTime ScannedUtc;

        static string FileName(string directory, string root) {
            using (var sha = SHA256.Create()) {
                var key = sha.ComputeHash(Encoding.UTF8.GetBytes(Safety.Normalize(root).ToUpperInvariant()));
                return Path.Combine(directory, BitConverter.ToString(key).Replace("-", "") + ".snapshot");
            }
        }

        internal static void Save(string directory, UsageResult result) {
            if (result.Canceled || result.Limited) throw new IOException("请完成同一范围的扫描后再保存基线。");
            Directory.CreateDirectory(directory);
            string destination = FileName(directory, result.Root.Path);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var gate = new FileStream(destination + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                try {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
                    using (var writer = new BinaryWriter(stream, Encoding.UTF8)) {
                        writer.Write(FormatVersion);
                        writer.Write(result.Root.Path);
                        writer.Write(result.ScannedUtc.Ticks);
                        var pending = new Stack<Tuple<UsageNode, int>>();
                        pending.Push(Tuple.Create(result.Root, -1));
                        int index = 0;
                        while (pending.Count > 0) {
                            var item = pending.Pop();
                            var node = item.Item1;
                            writer.Write(item.Item2);
                            writer.Write(node.Name);
                            writer.Write(node.Bytes);
                            writer.Write(node.Directory);
                            writer.Write(node.Partial);
                            writer.Write(node.Link);
                            writer.Write(node.ListingComplete);
                            foreach (var child in node.Children) pending.Push(Tuple.Create(child, index));
                            index++;
                        }
                    }
                    if (File.Exists(destination)) File.Replace(temporary, destination, null);
                    else File.Move(temporary, destination);
                } finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }

        internal static DiskSnapshot Load(string directory, string rootPath) {
            string file = FileName(directory, rootPath);
            if (!File.Exists(file)) throw new IOException("这个扫描范围还没有基线。请先保存一次，再重新扫描后对比。");
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536))
            using (var reader = new BinaryReader(stream, Encoding.UTF8)) {
                if (reader.ReadInt32() != FormatVersion) throw new IOException("无法识别基线格式，请重新保存。");
                string storedRoot = reader.ReadString();
                long ticks = reader.ReadInt64();
                if (!string.Equals(storedRoot, rootPath, StringComparison.OrdinalIgnoreCase) || ticks <= 0 || ticks > DateTime.MaxValue.Ticks)
                    throw new IOException("基线记录的路径或时间无效，请重新保存。");
                var nodes = new List<UsageNode>();
                while (stream.Position < stream.Length) {
                    int parentIndex = reader.ReadInt32();
                    var node = new UsageNode { Name = reader.ReadString(), Bytes = reader.ReadInt64(), Directory = reader.ReadBoolean(),
                        Partial = reader.ReadBoolean(), Link = reader.ReadBoolean(), ListingComplete = reader.ReadBoolean() };
                    if (nodes.Count >= MaxEntries || node.Bytes < 0 || parentIndex >= nodes.Count || parentIndex < -1
                        || (nodes.Count == 0) != (parentIndex == -1))
                        throw new IOException("基线目录结构无效，请重新保存。");
                    if (parentIndex == -1) node.Path = storedRoot;
                    else {
                        node.Parent = nodes[parentIndex];
                        if (!node.Parent.Directory || node.Name.Length == 0 || node.Name == "." || node.Name == ".."
                            || node.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                            throw new IOException("基线目录结构无效，请重新保存。");
                        node.Path = Path.Combine(node.Parent.Path, node.Name);
                        node.Parent.Children.Add(node);
                    }
                    nodes.Add(node);
                }
                if (nodes.Count == 0 || !nodes[0].Directory) throw new IOException("基线没有有效目录，请重新保存。");
                return new DiskSnapshot { Root = nodes[0], ScannedUtc = new DateTime(ticks, DateTimeKind.Utc) };
            }
        }
    }

    internal sealed class UsageChange {
        internal readonly UsageNode Before, After;
        readonly bool beforeKnown, afterKnown;

        internal UsageChange(UsageNode before, UsageNode after, bool beforeKnown = true, bool afterKnown = true) {
            Before = before; After = after;
            this.beforeKnown = beforeKnown; this.afterKnown = afterKnown;
        }

        internal UsageNode Item { get { return After ?? Before; } }
        internal bool HasDirectory { get { return IsDirectory(Before) || IsDirectory(After); } }
        static bool IsDirectory(UsageNode node) { return node != null && node.Directory && !node.Link; }
        static long? Total(UsageNode node, bool known) {
            if (node == null) return known ? (long?)0 : null;
            return node.Partial || node.Link ? null : (long?)node.Bytes;
        }
        internal long? Delta { get { return Total(After, afterKnown) - Total(Before, beforeKnown); } }
        internal string BeforeText { get { return SideText(Before, beforeKnown); } }
        internal string AfterText { get { return SideText(After, afterKnown); } }
        static string SideText(UsageNode node, bool known) { return node != null ? node.SizeText : known ? "无" : "未核实"; }

        internal string ChangeText {
            get {
                long? delta = Delta;
                if (!delta.HasValue) return "无法确定";
                if (delta.Value == 0) return Before == null ? "新增" : After == null ? "已移除" : "0 B";
                return (delta.Value > 0 ? "+" : "−") + FileSizes.Format(Math.Abs(delta.Value));
            }
        }

        internal List<UsageChange> Children() {
            var before = ChildrenByName(Before);
            var after = ChildrenByName(After);
            bool previousKnown = ListingKnown(Before, beforeKnown), currentKnown = ListingKnown(After, afterKnown);
            return before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).Select(name => {
                UsageNode earlier, later;
                before.TryGetValue(name, out earlier); after.TryGetValue(name, out later);
                return new UsageChange(earlier, later, previousKnown, currentKnown);
            }).OrderByDescending(row => row.Delta).ThenBy(row => row.Item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        static Dictionary<string, UsageNode> ChildrenByName(UsageNode node) {
            var entries = new Dictionary<string, UsageNode>(StringComparer.OrdinalIgnoreCase);
            if (IsDirectory(node)) foreach (var child in node.Children) entries[child.Name] = child;
            return entries;
        }

        static bool ListingKnown(UsageNode node, bool known) {
            return node == null ? known : !node.Link && (!node.Directory || node.ListingComplete);
        }
    }
}
