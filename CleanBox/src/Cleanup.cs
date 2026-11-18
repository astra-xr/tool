using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace CleanBox {
    internal sealed class CleanupItem {
        internal string Path;
        internal bool Directory;
        internal long Bytes;
        internal DateTime CreatedUtc, ModifiedUtc;
    }

    internal sealed class CleanupPlan {
        internal string Root;
        internal int Files, Directories;
        internal long Bytes;
        internal bool Test;
        internal List<CleanupItem> Items;

        internal static CleanupPlan Capture(string input, CancellationToken token, bool test = false) {
            string root = Safety.Check(input, test);
            var plan = new CleanupPlan { Root = root, Test = test, Items = new List<CleanupItem>() };
            foreach (string path in Safety.Plan(root, test, token)) {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("发现链接，清理预览已停止：" + path);
                bool directory = (attributes & FileAttributes.Directory) != 0;
                var item = new CleanupItem { Path = path, Directory = directory };
                if (directory) {
                    item.CreatedUtc = Directory.GetCreationTimeUtc(path);
                    plan.Directories++;
                } else {
                    var file = new FileInfo(path);
                    item.CreatedUtc = file.CreationTimeUtc;
                    item.ModifiedUtc = file.LastWriteTimeUtc;
                    item.Bytes = file.Length;
                    if (item.Bytes > long.MaxValue - plan.Bytes) throw new IOException("目标文件大小合计超过可统计范围。");
                    plan.Bytes += item.Bytes;
                    plan.Files++;
                }
                plan.Items.Add(item);
            }
            return plan;
        }

        internal void VerifyCurrent() {
            var current = Capture(Root, CancellationToken.None, Test);
            var byPath = current.Items.ToDictionary(item => item.Path, StringComparer.Ordinal);
            if (byPath.Count != Items.Count || Items.Any(item => {
                CleanupItem now;
                return !byPath.TryGetValue(item.Path, out now) || now.Directory != item.Directory || now.Bytes != item.Bytes
                    || now.CreatedUtc != item.CreatedUtc || now.ModifiedUtc != item.ModifiedUtc;
            })) throw new IOException("目标内容在确认后发生变化，请重新生成清理预览。");
        }

        internal string Preview {
            get {
                return Root + "\r\n\r\n" + Files + " 个文件 · " + Directories + " 个目录 · " + FileSizes.Format(Bytes)
                    + "（按文件长度合计）";
            }
        }
    }

    public class DeleteResult {
        public int Done, Skipped;
        public List<string> Failed = new List<string>();
        internal string ReportPath = "", ReportError = "";
        internal bool Canceled;
        internal string SuccessLabel = "成功";
        readonly List<string> rows = new List<string>();

        public string Summary { get { return SuccessLabel + " " + Done + " 项 · 跳过 " + Skipped + " 项 · 失败 " + Failed.Count + " 项"; } }

        internal void Success(string path, string status) { Done++; rows.Add(status + "\t" + path); }
        internal void Skip(string path, string reason) { Skipped++; rows.Add("跳过\t" + path + "\t" + reason); }
        internal void Fail(string path, string reason) { Failed.Add(path + "：" + reason); rows.Add("失败\t" + path + "\t" + reason); }

        internal void Record(string action, string root) {
            try {
                string directory = Path.Combine(Store.Data, "cleanup");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".txt");
                using (var writer = new StreamWriter(path, false, new UTF8Encoding(true))) {
                    writer.WriteLine(action);
                    writer.WriteLine(root);
                    writer.WriteLine(Summary);
                    writer.WriteLine();
                    writer.WriteLine("结果\t路径\t说明");
                    foreach (string row in rows) writer.WriteLine(row);
                }
                ReportPath = path;
            } catch (IOException ex) { ReportError = ex.Message; }
            catch (UnauthorizedAccessException ex) { ReportError = ex.Message; }
            Store.Log(action + " " + root + " · " + Summary + (ReportPath.Length > 0 ? " · 逐项记录 " + ReportPath : " · 逐项记录未保存 " + ReportError));
        }

        internal static string Reason(Exception error) {
            var native = error as Win32Exception;
            int code = native != null ? native.NativeErrorCode : (error.HResult & 0xFFFF);
            if (code == 32 || code == 33) return "文件被占用：" + error.Message;
            if (code == 5 || error is UnauthorizedAccessException) return "权限不足：" + error.Message;
            if (code == 2 || code == 3) return "目标已不存在：" + error.Message;
            return error.Message;
        }
    }
}
