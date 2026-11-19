using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace CleanBox {
    public enum AppSizeKind { Executable, RegisteredEstimate, InstallDirectory, EngineLauncher }
    internal enum RegistryStatus { Registered, Stale, Incomplete }
    public class AppEntry {
        public string Name = "", Version = "", Publisher = "", Source = "", Path = "", Executable = "", Command = "", Package = "", RegistryPath = "";
        public long? SizeBytes;
        public AppSizeKind SizeKind;
        public bool SizePartial;
        public bool SizeStale;
        public DateTime SizeMeasuredUtc;
        public string InstallLocation = "", SizeNote = "";
        public string SizeBadge {
            get {
                if(SizeStale)return "历史值 · 待重算";
                if(!SizeBytes.HasValue)return "未读取";
                string label=SizeKind==AppSizeKind.RegisteredEstimate?"登记估算":SizeKind==AppSizeKind.EngineLauncher?"启动文件":SizeKind==AppSizeKind.InstallDirectory?"目录实测":"程序文件";
                return SizePartial?label+" · 部分":label;
            }
        }
        public string SizeExplanation {
            get {
                if(SizeNote.Length>0)return SizeNote;
                if(Protected&&!SizeBytes.HasValue)return "系统组件，未自动计算目录大小。";
                if(Package.Length>0&&!SizeBytes.HasValue)return "商店应用未提供大小，安装目录由 Windows 管理。";
                if(SizeBytes.HasValue&&SizeKind==AppSizeKind.RegisteredEstimate)return "软件登记的估算值，可能未包含后续更新和缓存。";
                if(SizeBytes.HasValue&&SizeKind==AppSizeKind.Executable)return "仅此程序文件的长度，未包含同目录的其他文件及缓存。";
                return SizeBytes.HasValue?"按文件长度统计或使用安装记录，不包含目录之外的缓存及数据。":"未提供大小，尚未找到可信的本地安装目录。";
            }
        }
        public string SizeSource {
            get {
                if(!SizeBytes.HasValue)return "";
                switch(SizeKind) {
                    case AppSizeKind.RegisteredEstimate: return "安装记录估算";
                    case AppSizeKind.InstallDirectory: return SizePartial ? "安装目录（部分）" : "安装目录";
                    case AppSizeKind.EngineLauncher: return SizePartial ? "引擎启动文件（部分）" : "仅引擎启动文件";
                    default: return "仅程序文件";
                }
            }
        }
        public RegistryHive Hive; public RegistryView View; public bool Protected;
        internal RegistryStatus Registration;
        internal string SearchText;
        internal string ReportedDescription = "";
        internal bool IsPackageFramework;
        internal string QuietCommand = "", ModifyCommand = "";
        internal bool NeedsRegistrationReview { get { return Registration != RegistryStatus.Registered; } }
        internal string DisplaySource { get { return NeedsRegistrationReview ? RegistrationLabel : Source; } }
        internal string RegistrationLabel { get { return Registration == RegistryStatus.Stale ? "失效登记" : "登记待核实"; } }
        public string Id { get { return Package.Length > 0 ? "appx:" + Package : RegistryPath.Length > 0 ? Hive + ":" + View + ":" + RegistryPath : "exe:" + Executable.ToLowerInvariant(); } }
    }
    internal static class FileSizes {
        static readonly string[] units = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };

        internal static string Format(long bytes) {
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return value.ToString("0.##") + " " + units[unit];
        }

        internal static long? FromEstimatedKilobytes(object value) {
            if (value is int) {
                // RegistryKey exposes REG_DWORD as a signed int.
                uint kilobytes = unchecked((uint)(int)value);
                return kilobytes == 0 ? (long?)null : kilobytes * 1024L;
            }
            long number;
            if (long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.None,
                CultureInfo.InvariantCulture, out number) && number > 0 && number <= long.MaxValue / 1024)
                return number * 1024;
            return null;
        }

        internal static long? ReadLength(string path) {
            try {
                var file = new FileInfo(path);
                if ((file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0) return file.Length;
            } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }
    }
    public static class Store {
        public static readonly string Home = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        public static readonly string Data = System.IO.Path.Combine(Home, "data");
        static readonly object gate = new object();
        public static event Action<string> Logged;
        public static void Log(string s) {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s;
            lock(gate) { try { Directory.CreateDirectory(Data); File.AppendAllText(System.IO.Path.Combine(Data, "operations.log"), line + Environment.NewLine, Encoding.UTF8); } catch {} }
            var h = Logged; if(h != null) h(line);
        }
        public static string RunPowerShell(string script, CancellationToken token, int timeout = 90000) {
            string exe = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\WindowsPowerShell\v1.0\powershell.exe");
            var psi = new ProcessStartInfo(exe, "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.UTF8Encoding]::new(); $ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; try { " + script + " } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }")));
            psi.UseShellExecute=false; psi.CreateNoWindow=true; psi.RedirectStandardOutput=true; psi.RedirectStandardError=true; psi.StandardOutputEncoding=Encoding.UTF8; psi.StandardErrorEncoding=Encoding.UTF8;
            using(var p=Process.Start(psi)) {
                Task<string> output=p.StandardOutput.ReadToEndAsync(), errors=p.StandardError.ReadToEndAsync(); var clock=Stopwatch.StartNew();
                while(!p.WaitForExit(150)) { if(token.IsCancellationRequested || clock.ElapsedMilliseconds > timeout) { try { p.Kill(); } catch {} token.ThrowIfCancellationRequested(); throw new TimeoutException("Windows 应用查询超时，请重试。"); } }
                Task.WaitAll(output,errors); if(p.ExitCode!=0) throw new IOException(errors.Result.Trim()); return output.Result;
            }
        }
        public static string QuotePS(string s) { return "'"+s.Replace("'","''")+"'"; }
        public static void Export(IEnumerable<AppEntry> apps,string file) {
            Func<string,string> q=s=>{s=s??"";if(s.Length>0&&"=+-@\t\r\n".IndexOf(s[0])>=0)s="'"+s;return "\""+s.Replace("\"","\"\"")+"\"";};
            var lines=new List<string>{"名称,版本,发布者,来源,安装位置,程序路径,大小（字节）,大小依据,大小说明"};
            foreach(var a in apps) lines.Add(string.Join(",",new[]{a.Name,a.Version,a.Publisher,a.DisplaySource,a.Path,a.Executable,a.SizeBytes.HasValue?a.SizeBytes.Value.ToString(CultureInfo.InvariantCulture):"",a.SizeSource,a.SizeExplanation}.Select(q)));
            File.WriteAllLines(file,lines, new UTF8Encoding(true));
        }
    }
    public static class Scanner {
        internal const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        static readonly HashSet<string> ExcludedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            ".git", "node_modules", ".venv", "__pycache__", "$RECYCLE.BIN", "System Volume Information"
        };
        public static void Registered(Action<AppEntry> add, Action<string> status, CancellationToken token) {
            Registered(add, status, token, false);
        }
        internal static void Registered(Action<AppEntry> add, Action<string> status, CancellationToken token, bool verifyOnly) {
            var sizes=verifyOnly ? null : new InstalledSizeLookup();
            foreach(var hive in new[]{RegistryHive.LocalMachine,RegistryHive.CurrentUser}) foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32}) {
                token.ThrowIfCancellationRequested(); if(status!=null) status("读取 " + hive + " · " + view);
                try { using(var b=RegistryKey.OpenBaseKey(hive,view)) using(var root=b.OpenSubKey(Uninstall)) {
                    if(root==null) continue;
                    foreach(var key in root.GetSubKeyNames()) { token.ThrowIfCancellationRequested(); try { using(var k=root.OpenSubKey(key)) {
                        if(k==null) continue;
                        var a=ReadRegistration(k, !verifyOnly);
                        if(string.IsNullOrWhiteSpace(a.Name)) continue;
                        a.Source=(hive==RegistryHive.LocalMachine?"已安装 · 全机":"已安装 · 当前用户")+(view==RegistryView.Registry64?" · 64位":" · 32位");
                        a.Hive=hive; a.View=view; a.RegistryPath=Uninstall+"\\"+key;
                        a.Path=CleanPath(a.InstallLocation);
                        if(a.Path.Length==0 && File.Exists(a.Executable)) a.Path=System.IO.Path.GetDirectoryName(a.Executable);
                        if(a.Path.Length==0) { string exe,args; if(ParseCommand(a.Command,out exe,out args) && System.IO.Path.IsPathRooted(exe) && File.Exists(exe)) a.Path=System.IO.Path.GetDirectoryName(exe); }
                        if(!verifyOnly) {
                            if(!a.SizeBytes.HasValue&&!a.Protected&&!a.NeedsRegistrationReview) {
                                try { sizes.Fill(a,a.InstallLocation,token); }
                                catch(IOException) { a.SizeNote="目录不可访问或路径无效。"; }
                                catch(UnauthorizedAccessException) { a.SizeNote="没有读取安装目录的权限。"; }
                                catch(System.Security.SecurityException) { a.SizeNote="系统限制了安装目录的读取权限。"; }
                            }
                        }
                        add(a);
                    }} catch(OperationCanceledException) { throw; } catch(Exception e) { if(verifyOnly) throw; Store.Log("跳过安装记录 " + key + "："+e.Message); } }
                }} catch(OperationCanceledException) { throw; } catch(Exception e) { if(verifyOnly) throw; Store.Log("注册表读取失败："+e.Message); }
            }
        }
        internal static string V(RegistryKey k,string name) { return Convert.ToString(k.GetValue(name,"")); }

        internal static AppEntry ReadRegistration(RegistryKey key, bool assess) {
            string icon = IconFile(V(key, "DisplayIcon"));
            var app = new AppEntry {
                Name = V(key, "DisplayName"), Version = V(key, "DisplayVersion"), Publisher = V(key, "Publisher"),
                Protected = V(key, "SystemComponent") == "1", InstallLocation = V(key, "InstallLocation"),
                Executable = CleanPath(icon), Command = V(key, "UninstallString"),
                QuietCommand = V(key, "QuietUninstallString"), ModifyCommand = V(key, "ModifyPath")
            };
            if (assess && !string.IsNullOrWhiteSpace(app.Name)) {
                app.ReportedDescription = DescribeMetadata(V(key, "Comments"));
                app.SizeBytes = FileSizes.FromEstimatedKilobytes(key.GetValue("EstimatedSize"));
                app.SizeKind = AppSizeKind.RegisteredEstimate;
                app.Registration = AssessRegistration(app, icon, V(key, "WindowsInstaller") == "1");
            }
            return app;
        }

        static string DescribeMetadata(string value) {
            string description = Regex.Replace(value ?? "", @"[\p{Cc}\s]+", " ").Trim();
            return description.Length > 240 ? description.Substring(0, 239) + "…" : description;
        }

        internal static RegistryStatus AssessRegistration(AppEntry app, string icon, bool windowsInstaller) {
            if (app.Protected || windowsInstaller) return RegistryStatus.Registered;
            var paths = new List<string> { app.InstallLocation, icon };
            foreach (string command in new[] { app.Command, app.QuietCommand, app.ModifyCommand }) {
                if (string.IsNullOrWhiteSpace(command)) continue;
                string exe, args;
                if (!ParseCommand(command, out exe, out args) ||
                    Regex.IsMatch(exe, @"(^|[\\/])msiexec\.exe$", RegexOptions.IgnoreCase)) return RegistryStatus.Registered;
                paths.Add(exe);
            }
            var evidence = paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (evidence.Length > 0) return evidence.All(IsMissingLocalPath) ? RegistryStatus.Stale : RegistryStatus.Registered;
            return string.IsNullOrWhiteSpace(app.Version) && !app.SizeBytes.HasValue ? RegistryStatus.Incomplete : RegistryStatus.Registered;
        }

        internal static bool IsMissingLocalPath(string value) {
            if (string.IsNullOrWhiteSpace(value)) return false;
            try {
                string path = Safety.Normalize(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')));
                var drive = new DriveInfo(Path.GetPathRoot(path));
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) return false;
                try { File.GetAttributes(path); return false; }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                // A missing child under an inaccessible or redirected directory is inconclusive.
                for (string parent = Path.GetDirectoryName(path); parent != null; parent = Path.GetDirectoryName(parent)) {
                    try {
                        var attributes = File.GetAttributes(parent);
                        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0) return false;
                    } catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                }
                return true;
            } catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }

        public static string CleanPath(string path) {
            try {
                path = Environment.ExpandEnvironmentVariables((path ?? "").Trim().Trim('"'));
                return string.IsNullOrWhiteSpace(path) ? "" : Safety.TrimTrailingSeparators(System.IO.Path.GetFullPath(path));
            } catch { return ""; }
        }
        static string IconFile(string p) { return Regex.Replace(p??"",@",\s*-?\d+\s*$", ""); }
        public static bool ParseCommand(string command,out string exe,out string args) {
            exe="";args=""; string s=Environment.ExpandEnvironmentVariables((command??"").Trim());
            if(s.StartsWith("\"")) { int end=s.IndexOf('"',1); if(end<2) return false; exe=s.Substring(1,end-1);args=s.Substring(end+1).Trim(); }
            else { var m=Regex.Match(s,@"^(.+?\.exe)(?:\s+(.*))?$",RegexOptions.IgnoreCase); if(!m.Success) return false; exe=m.Groups[1].Value;args=m.Groups[2].Value; }
            return exe.EndsWith(".exe",StringComparison.OrdinalIgnoreCase);
        }
        public static void Packages(Action<AppEntry> add, Action<string> status, CancellationToken token) {
            status("读取当前用户的 Microsoft Store / MSIX 应用…");
            string json=Store.RunPowerShell("@(Get-AppxPackage | Select-Object Name,Version,Publisher,InstallLocation,PackageFullName,IsFramework,NonRemovable) | ConvertTo-Json -Compress",token);
            if(string.IsNullOrWhiteSpace(json)) return;
            var ser=new JavaScriptSerializer{MaxJsonLength=16777216}; var obj=ser.DeserializeObject(json); var list=obj as object[] ?? new[]{obj};
            foreach(var item in list) { token.ThrowIfCancellationRequested(); var d=item as Dictionary<string,object>; if(d==null) continue; Func<string,string> v=k=>d.ContainsKey(k)?Convert.ToString(d[k]):"";
                bool framework=v("IsFramework")=="True";
                add(new AppEntry{Name=v("Name"),Version=v("Version"),Publisher=v("Publisher"),Path=v("InstallLocation"),InstallLocation=v("InstallLocation"),Package=v("PackageFullName"),Source="商店 / MSIX · 当前用户",IsPackageFramework=framework,Protected=framework||v("NonRemovable")=="True"});
            }
        }
        public static void Shortcuts(Action<AppEntry> add,Action<string> status,CancellationToken token) {
            status("检查开始菜单和桌面快捷方式…");
            var roots=new[]{Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)};
            Type t=Type.GetTypeFromProgID("WScript.Shell"); if(t==null) return; dynamic shell=Activator.CreateInstance(t);
            try { foreach(string root in roots) foreach(string file in WalkFiles(root,"*.lnk",token,null,12)) {
                object shortcut=null; try { shortcut=shell.CreateShortcut(file); dynamic l=shortcut; string p=Convert.ToString(l.TargetPath);
                    if(!p.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)||!File.Exists(p)||Safety.IsSystemTree(p)) continue;
                    add(FromExe(p,"快捷方式 · 待核对",System.IO.Path.GetFileNameWithoutExtension(file)));
                } catch {} finally { if(shortcut!=null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut); }
            }} finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
        public static AppEntry FromExe(string p,string source,string title=null) {
            var a=new AppEntry{Executable=p,Path=System.IO.Path.GetDirectoryName(p),Source=source,Name=title??System.IO.Path.GetFileNameWithoutExtension(p)};
            a.SizeBytes=FileSizes.ReadLength(p);
            try { var v=FileVersionInfo.GetVersionInfo(p); if(title==null&&!string.IsNullOrWhiteSpace(v.ProductName)) a.Name=v.ProductName.Trim(); a.Version=v.ProductVersion??"";a.Publisher=v.CompanyName??"";a.ReportedDescription=DescribeMetadata(v.FileDescription); } catch {}
            return a;
        }
        public static IEnumerable<string> WalkFiles(string root,string pattern,CancellationToken token,Action<string> status,int maxDepth=int.MaxValue) {
            // Framework wildcard matching can include longer extensions for patterns such as *.exe.
            string extension=Regex.IsMatch(pattern,@"^\*\.[A-Za-z0-9]+$")?pattern.Substring(1):null;
            var pending=new Stack<Tuple<string,int>>(); pending.Push(Tuple.Create(root,0));
            while(pending.Count>0) { token.ThrowIfCancellationRequested(); var node=pending.Pop(); string dir=node.Item1;
                string[] files=new string[0],dirs=new string[0];
                try { if(!Directory.Exists(dir)||Safety.HasReparseAncestor(dir)||Safety.IsSystemTree(dir)) continue; files=Directory.GetFiles(dir,pattern); if(node.Item2<maxDepth) dirs=Directory.GetDirectories(dir); }
                catch(UnauthorizedAccessException) { if(status!=null) status("无权限，已跳过："+dir); Store.Log("扫描跳过（无权限）："+dir); continue; }
                catch(IOException e) { Store.Log("扫描跳过："+dir+"；"+e.Message); continue; }
                if(status!=null) status("扫描："+dir);
                foreach(var f in files) {
                    token.ThrowIfCancellationRequested();
                    if(extension==null||string.Equals(System.IO.Path.GetExtension(f),extension,StringComparison.OrdinalIgnoreCase)) yield return f;
                }
                foreach(var d in dirs) if(!ExcludedDirectories.Contains(System.IO.Path.GetFileName(d))) pending.Push(Tuple.Create(d,node.Item2+1));
            }
        }
        public static void Disk(string root,Action<AppEntry> add,Action<string> status,CancellationToken token) {
            foreach(string exe in WalkFiles(root,"*.exe",token,status)) {
                if(Safety.Within(exe,Store.Home)) continue;
                string name=System.IO.Path.GetFileNameWithoutExtension(exe);
                if(Regex.IsMatch(name,@"^(unins\d*|uninstall|setup|update|updater|crashpad_handler|crashreporter|vcredist.*|vc_redist.*)$",RegexOptions.IgnoreCase)) continue;
                add(FromExe(exe,"磁盘发现 · 待核对"));
            }
        }
    }
    public static class Safety {
        public static string Normalize(string p) {
            if(string.IsNullOrWhiteSpace(p)||!Regex.IsMatch(p,@"^[A-Za-z]:[\\/]")) throw new IOException("请选择本机磁盘上的绝对路径。");
            if(p.Substring(2).Contains(":")) throw new IOException("不支持设备路径或文件数据流。");
            foreach(var part in p.Replace('/','\\').Substring(2).Split('\\')) if(part.EndsWith(".")||part.EndsWith(" ")||Regex.IsMatch(part,@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)",RegexOptions.IgnoreCase)) throw new IOException("路径包含不明确或保留的名称，无法安全处理。");
            string n=TrimTrailingSeparators(System.IO.Path.GetFullPath(p));
            foreach(var part in n.Substring(2).Split('\\')) if(part.EndsWith(".")||part.EndsWith(" ")) throw new IOException("路径尾部有空格或点，无法安全处理。");
            return n;
        }
        internal static string TrimTrailingSeparators(string fullPath) {
            return fullPath.Length == System.IO.Path.GetPathRoot(fullPath).Length ? fullPath : fullPath.TrimEnd('\\');
        }
        public static bool Within(string p,string parent) { if(string.IsNullOrEmpty(p)||string.IsNullOrEmpty(parent)) return false; p=p.TrimEnd('\\');parent=parent.TrimEnd('\\');return p.Equals(parent,StringComparison.OrdinalIgnoreCase)||p.StartsWith(parent+"\\",StringComparison.OrdinalIgnoreCase); }
        public static bool HasReparseAncestor(string p) {
            try { for(string n=System.IO.Path.GetFullPath(p);!string.IsNullOrEmpty(n);n=System.IO.Path.GetDirectoryName(n)) if((Directory.Exists(n)||File.Exists(n))&&(File.GetAttributes(n)&FileAttributes.ReparsePoint)!=0) return true; }
            catch { return true; } return false;
        }
        public static bool IsSystemTree(string p) {
            string win=Environment.GetFolderPath(Environment.SpecialFolder.Windows), pd=Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var roots=new[]{win,System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"WindowsApps"),System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Windows Defender"),System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Windows NT"),System.IO.Path.Combine(pd,"Microsoft"),System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Packages")};
            if(roots.Any(r=>Within(p,r))) return true;
            return Regex.IsMatch(p,@"^[A-Za-z]:\\(\$RECYCLE\.BIN|System Volume Information|Recovery|Boot|Config\.Msi)(\\|$)",RegexOptions.IgnoreCase);
        }
        internal static bool IsSharedDirectory(string p) {
            string user=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var shared=new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),user,Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),System.IO.Path.Combine(user,"Downloads"),System.IO.Path.Combine(user,"AppData"),System.IO.Path.GetDirectoryName(user)};
            return shared.Where(s=>!string.IsNullOrEmpty(s)).Any(s=>Within(s,p))||new[]{"Program Files","Program Files (x86)","ProgramData","Users","Windows Kits","WindowsApps","Programs","Common Files"}.Contains(System.IO.Path.GetFileName(p),StringComparer.OrdinalIgnoreCase);
        }
        public static string Check(string input,bool allowTest=false) {
            string p=Normalize(input); if(p.Length<4||System.IO.Path.GetDirectoryName(p)==null) throw new IOException("禁止删除磁盘根目录。");
            if(IsSystemTree(p)||Regex.IsMatch(p,@"^[A-Za-z]:\\(pagefile\.sys|swapfile\.sys|hiberfil\.sys|bootmgr|BOOTNXT|DumpStack\.log(\.tmp)?)$",RegexOptions.IgnoreCase)) throw new IOException("这是受保护的系统位置。商店应用请使用正常卸载。");
            if(IsSharedDirectory(p)) throw new IOException("这是共享目录或用户资料根目录，请选择更具体的软件文件夹。");
            if((Within(Store.Home,p)||Within(p,Store.Home))&&!(allowTest&&Within(p,System.IO.Path.Combine(Store.Home,"tests")))) throw new IOException("不能删除清理匣自身及其父目录。");
            if(HasReparseAncestor(p)) throw new IOException("目标或父目录是链接/联接点。请定位真实目录后再处理。");
            if(!Directory.Exists(p)&&!File.Exists(p)) throw new FileNotFoundException("目标已经不存在。",p);
            return p;
        }
        public static List<string> Plan(string root,bool test=false,CancellationToken token=default(CancellationToken)) {
            root=Check(root,test); var result=new List<string>(); var stack=new Stack<string>();stack.Push(root);
            while(stack.Count>0) { token.ThrowIfCancellationRequested(); string p=stack.Pop(); if(!Within(p,root)||HasReparseAncestor(p)) throw new IOException("检测到越界或目录链接，操作停止："+p);
                result.Add(p); if(Directory.Exists(p)) foreach(string child in Directory.EnumerateFileSystemEntries(p)) { token.ThrowIfCancellationRequested(); if(IsSystemTree(child)) throw new IOException("目录内包含受保护项目，操作停止。"); stack.Push(child); }
            }
            return result.OrderByDescending(s=>s.Length).ToList();
        }
    }
    public static class Operations {
        public static DeleteResult Permanent(string root,bool test=false) {
            return Permanent(CleanupPlan.Capture(root,CancellationToken.None,test), false);
        }
        internal static DeleteResult Permanent(CleanupPlan plan, bool verify = true) {
            if (verify) plan.VerifyCurrent();
            return DeleteItems(plan, false);
        }
        public static DeleteResult Reboot(string root) {
            return Reboot(CleanupPlan.Capture(root,CancellationToken.None), false);
        }
        internal static DeleteResult Reboot(CleanupPlan plan, bool verify = true) {
            if(!Native.IsAdmin) throw new UnauthorizedAccessException("重启后删除需要管理员权限，请在设置中选择“以管理员重启”。");
            if (verify) plan.VerifyCurrent();
            return DeleteItems(plan, true);
        }
        static DeleteResult DeleteItems(CleanupPlan plan, bool reboot) {
            var result = new DeleteResult { SuccessLabel = reboot ? "已登记重启删除" : "已删除" };
            foreach (var item in plan.Items) try {
                if(!Safety.Within(item.Path,plan.Root)||Safety.HasReparseAncestor(item.Path)) throw new IOException("路径边界已变化。");
                var attributes = File.GetAttributes(item.Path);
                if (((attributes & FileAttributes.Directory) != 0) != item.Directory) throw new IOException("目标类型已变化。");
                if (reboot) {
                    if (!Native.MoveFileEx(item.Path,null,4)) throw new System.ComponentModel.Win32Exception();
                    result.Success(item.Path,"已登记重启删除");
                } else {
                    if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(item.Path, attributes & ~FileAttributes.ReadOnly);
                    if (item.Directory) Directory.Delete(item.Path,false); else File.Delete(item.Path);
                    result.Success(item.Path,"已删除");
                }
            } catch (FileNotFoundException) { result.Skip(item.Path,"目标已不存在。"); }
            catch (DirectoryNotFoundException) { result.Skip(item.Path,"目标已不存在。"); }
            catch (Exception ex) { result.Fail(item.Path,DeleteResult.Reason(ex)); }
            result.Record(reboot ? "登记重启删除" : "永久删除", plan.Root);
            return result;
        }
        public static void Recycle(string root) {
            var result = Recycle(CleanupPlan.Capture(root, CancellationToken.None), null, false);
            if (result.Canceled) throw new OperationCanceledException();
            if (result.Failed.Count > 0) throw new IOException(result.Failed[0]);
            if (result.Skipped > 0) throw new IOException("部分位置未能核实回收结果。");
        }
        internal static DeleteResult Recycle(CleanupPlan plan, Action beforeRecycle = null, bool verify = true) {
            if (beforeRecycle != null) beforeRecycle();
            if (verify || beforeRecycle != null) plan.VerifyCurrent();
            var result = new DeleteResult { SuccessLabel = "已提交回收站并移出原处" };
            Exception shellError = null;
            try {
                if (Directory.Exists(plan.Root)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(plan.Root,Microsoft.VisualBasic.FileIO.UIOption.AllDialogs,Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(plan.Root,Microsoft.VisualBasic.FileIO.UIOption.AllDialogs,Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
            } catch (OperationCanceledException) { result.Canceled = true; }
            catch (IOException ex) { shellError = ex; }
            catch (UnauthorizedAccessException ex) { shellError = ex; }
            Action<string> recordMissing = path => {
                if (result.Canceled || shellError != null) result.Skip(path,"原路径已不存在，回收结果未能核实。");
                else result.Success(path,"已提交回收站并移出原处");
            };
            foreach (var item in plan.Items) {
                try {
                    File.GetAttributes(item.Path);
                    if (result.Canceled) result.Skip(item.Path,"已取消，原路径仍存在。");
                    else result.Fail(item.Path,shellError != null ? DeleteResult.Reason(shellError) : "原路径仍存在。");
                } catch (FileNotFoundException) { recordMissing(item.Path); }
                catch (DirectoryNotFoundException) { recordMissing(item.Path); }
                catch (IOException ex) { result.Fail(item.Path,"无法核实处理结果：" + DeleteResult.Reason(ex)); }
                catch (UnauthorizedAccessException ex) { result.Fail(item.Path,"无法核实处理结果：" + DeleteResult.Reason(ex)); }
            }
            result.Record("Windows 回收站处理",plan.Root);
            return result;
        }
        public static void Uninstall(AppEntry a) {
            if(a.Protected) throw new IOException("此项被 Windows 标记为系统组件，本工具不卸载它。");
            if(a.Package.Length>0) { Store.RunPowerShell("Remove-AppxPackage -Package "+Store.QuotePS(a.Package)+" -ErrorAction Stop",CancellationToken.None,180000);Store.Log("已卸载当前用户的商店应用："+a.Name); return; }
            string exe,args; if(!Scanner.ParseCommand(a.Command,out exe,out args)) throw new IOException("没有可识别的卸载入口。可打开安装目录核对，或使用文件处理功能。");
            if(System.IO.Path.GetFileName(exe).Equals("msiexec.exe",StringComparison.OrdinalIgnoreCase)) { var m=Regex.Match(args,@"\{[0-9a-fA-F-]{36}\}"); if(!m.Success) throw new IOException("无法识别 MSI 产品编号。");exe=System.IO.Path.Combine(Environment.SystemDirectory,"msiexec.exe");args="/x "+m.Value; }
            else { if(!System.IO.Path.IsPathRooted(exe)||!File.Exists(exe)) throw new IOException("卸载程序不存在："+exe); if(new[]{"cmd.exe","powershell.exe","pwsh.exe","wscript.exe","cscript.exe","rundll32.exe"}.Contains(System.IO.Path.GetFileName(exe),StringComparer.OrdinalIgnoreCase)) throw new IOException("卸载入口是脚本或系统命令，请从 Windows 设置中核对处理。"); }
            Process.Start(new ProcessStartInfo(exe,args){UseShellExecute=true,WorkingDirectory=System.IO.Path.GetDirectoryName(exe)});Store.Log("已启动卸载器（完成状态由原卸载器决定）："+a.Name);
        }
        public static List<string> Residuals(AppEntry a) {
            var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase); Action<string> add=p=>{try { if(Directory.Exists(p)||File.Exists(p)) paths.Add(Safety.Check(p)); }catch{} };
            if(a.Path.Length>0)add(a.Path);
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase); if(a.Name.Length>=4&&a.Name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars())<0) names.Add(a.Name);
            if(a.Path.Length>0) { string name=System.IO.Path.GetFileName(a.Path);if(name.Length>=4)names.Add(name); }
            foreach(string root in new[]{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)}) foreach(string name in names) add(System.IO.Path.Combine(root,name));
            return paths.OrderBy(x=>x).ToList();
        }
        public static void RemoveStaleRecord(AppEntry a) {
            string prefix=Scanner.Uninstall+"\\";
            if(!a.RegistryPath.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||a.RegistryPath.Length==prefix.Length
                ||a.RegistryPath.IndexOfAny(new[]{'\\','"'},prefix.Length)>=0||(a.Hive!=RegistryHive.LocalMachine&&a.Hive!=RegistryHive.CurrentUser)
                ||(a.View!=RegistryView.Registry64&&a.View!=RegistryView.Registry32)) throw new IOException("不是可清理的卸载记录。");
            if(a.Protected) throw new IOException("不能清理系统组件记录。");
            string backup=System.IO.Path.Combine(Store.Data,"registry-backup-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N")+".reg");
            using(var b=RegistryKey.OpenBaseKey(a.Hive,a.View)) {
                ValidateStaleRecord(a,b);
                Directory.CreateDirectory(Store.Data);
                string root=a.Hive==RegistryHive.LocalMachine?"HKLM":"HKCU";
                var psi=new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory,"reg.exe"),"export \""+root+"\\"+a.RegistryPath+"\" \""+backup+"\" /reg:"+(a.View==RegistryView.Registry64?"64":"32")){UseShellExecute=false,CreateNoWindow=true};
                using(var p=Process.Start(psi)){p.WaitForExit();if(p.ExitCode!=0||!File.Exists(backup))throw new IOException("注册表备份失败，未删除记录。");}
                ValidateStaleRecord(a,b);
                b.DeleteSubKeyTree(a.RegistryPath);
            }
            Store.Log("清理失效卸载记录："+a.Name+"；备份："+backup);
        }

        static void ValidateStaleRecord(AppEntry app,RegistryKey hive) {
            using(var key=hive.OpenSubKey(app.RegistryPath)) {
                if(key==null) throw new IOException("卸载记录已经不存在。");
                var current = Scanner.ReadRegistration(key, true);
                if (current.Name != app.Name || current.Version != app.Version || current.InstallLocation != app.InstallLocation
                    || current.Command != app.Command || current.Executable != app.Executable
                    || current.QuietCommand != app.QuietCommand || current.ModifyCommand != app.ModifyCommand)
                    throw new IOException("卸载记录已经变化，请重新扫描。");
                EnsureStaleRegistration(current, app.Path);
            }
        }

        internal static void EnsureStaleRegistration(AppEntry current, string inferredLocation) {
            if (current.Protected || current.Registration != RegistryStatus.Stale
                || (!string.IsNullOrWhiteSpace(inferredLocation) && !Scanner.IsMissingLocalPath(inferredLocation)))
                throw new IOException("软件仍有安装迹象，或路径无法核实。已保留卸载记录。");
        }
    }
}
