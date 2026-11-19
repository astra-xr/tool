using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
namespace CleanBox {
    public partial class Tests {
        static List<string> results=new List<string>();
        static void Check(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);results.Add("PASS: "+name);}
        static void Reject(Action action,string name){bool rejected=false;try{action();}catch{rejected=true;}Check(rejected,name);}
        public static void Run(){
            results.Clear();Directory.CreateDirectory(Store.Data);
            string root=Path.Combine(Store.Home,"tests","run-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            Check(Safety.Within(root,Path.Combine(Store.Home,"tests")),"Fixture is inside the designated test directory");
            Reject(()=>Safety.Check(@"D:\"),"Drive root protected");
            Reject(()=>Safety.Check(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),"Windows protected");
            Reject(()=>Safety.Check(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),"User profile protected");
            Reject(()=>Safety.Check(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),"Shared application root protected");
            Reject(()=>Safety.Check(Store.Home),"CleanBox protects itself");
            Reject(()=>Safety.Normalize(@"\\server\share\folder"),"UNC / remote targets rejected");
            Reject(()=>Safety.Normalize(@"D:\a.txt:stream"),"Alternate data streams rejected");
            Reject(()=>Safety.Normalize(@"D:\folder.\file"),"Ambiguous trailing-dot path rejected");
            Check(!Safety.Within(@"D:\AppSibling\file",@"D:\App"),"Path prefix collision cannot cross boundary");
            string exe,arg;
            Check(Scanner.ParseCommand("\"D:\\App Name\\uninstall.exe\" /remove",out exe,out arg)&&exe==@"D:\App Name\uninstall.exe"&&arg=="/remove","Quoted uninstall path parsed");
            Check(Scanner.ParseCommand(@"D:\App Name\uninstall.exe /remove",out exe,out arg)&&exe==@"D:\App Name\uninstall.exe","Unquoted path with spaces parsed");
            Check(!Scanner.ParseCommand("https://example.com/remove",out exe,out arg),"Non-program uninstall string rejected");
            Check(Store.QuotePS("a'b")=="'a''b'","PowerShell literal quoting preserves apostrophes");
            Reject(()=>Safety.Check(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory),"pagefile.sys")),"System paging file protected");
            VerifyV243(root);
            VerifyV244(root);
            VerifyV250(root);
            VerifyRegistrationVisibility(root);
            VerifyV260(root);
            VerifyUninstallAdvice();
            string linkHolder=Path.Combine(root,"link-holder"),destination=Path.Combine(root,"sentinel"),junction=Path.Combine(linkHolder,"jump");Directory.CreateDirectory(linkHolder);Directory.CreateDirectory(destination);File.WriteAllText(Path.Combine(destination,"keep.txt"),"must survive link rejection");
            Store.RunPowerShell("New-Item -ItemType Junction -Path "+Store.QuotePS(junction)+" -Target "+Store.QuotePS(destination)+" | Out-Null",CancellationToken.None);
            File.WriteAllBytes(Path.Combine(linkHolder,"local.bin"),new byte[4096]);
            var partialSize=Native.FindLocks(linkHolder,CancellationToken.None);
            Check(partialSize.SizeBytes==4096&&partialSize.SizePartial,"Directory size excludes linked files and reports the measured subtotal");
            var lookup=new InstalledSizeLookup();
            var missingEstimate=new AppEntry();
            lookup.Fill(missingEstimate,linkHolder,CancellationToken.None);
            Check(missingEstimate.SizeBytes==4096&&missingEstimate.SizePartial&&missingEstimate.SizeKind==AppSizeKind.InstallDirectory,"Missing install estimate falls back to a bounded directory subtotal without following links");
            string launcherDir=Path.Combine(root,"MobileAppEngine","app","com.example");Directory.CreateDirectory(launcherDir);
            var engineApp=new AppEntry{Executable=Path.Combine(launcherDir,"icon.ico"),Command="\""+Path.Combine(launcherDir,"uninstall.exe")+"\""};
            File.WriteAllBytes(engineApp.Executable,new byte[64]);File.WriteAllBytes(Path.Combine(launcherDir,"uninstall.exe"),new byte[32]);
            lookup.Fill(engineApp,"",CancellationToken.None);
            Check(engineApp.SizeBytes==96&&engineApp.SizeKind==AppSizeKind.EngineLauncher&&!engineApp.SizePartial,"Matching icon and uninstall paths identify engine launcher files without claiming whole-app size");
            string reason;
            Check(InstalledSizeLookup.ResolveDirectory(new AppEntry{Path=launcherDir,Executable=engineApp.Executable},out reason)==null&&reason.Length>0,"A lone icon or inferred action path cannot authorize whole-directory size measurement");
            Check(InstalledSizeLookup.ResolveDirectory(new AppEntry{InstallLocation=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)},out reason)==null,"Size measurement rejects a shared software root");
            var bounded=InstalledSizeLookup.Measure(launcherDir,CancellationToken.None,1,30000,null);
            var complete=InstalledSizeLookup.Measure(launcherDir,CancellationToken.None,100,30000,null);
            Check(bounded.Partial&&bounded.Bytes.HasValue&&complete.Bytes==96&&!complete.Partial,"A larger single-item budget replaces a bounded subtotal with a complete result");
            var canceledSize=new CancellationTokenSource();canceledSize.Cancel();bool sizeCanceled=false;
            try{InstalledSizeLookup.Measure(launcherDir,canceledSize.Token,100,30000,null);}catch(OperationCanceledException){sizeCanceled=true;}finally{canceledSize.Dispose();}
            Check(sizeCanceled,"Directory measurement honors cancellation before work");
            Check(MainForm.MatchesSize(new AppEntry{SizeBytes=1073741825L},1)&&!MainForm.MatchesSize(new AppEntry{SizeBytes=1073741824L},1)&&MainForm.MatchesSize(new AppEntry{SizeBytes=1073741824L},2)&&MainForm.MatchesSize(new AppEntry(),4),"Size ranges have explicit inclusive boundaries and distinguish unknown values");
            string configs=Path.Combine(root,"configs"),exact=Path.Combine(configs,"ReviewFixture"),similar=Path.Combine(configs,"ReviewFixtureOther");
            Directory.CreateDirectory(exact);Directory.CreateDirectory(similar);File.WriteAllBytes(Path.Combine(exact,"settings.dat"),new byte[80]);
            var reviewApp=new AppEntry{Name="ReviewFixture",Path=launcherDir};
            var leftovers=ResidualReview.Find(reviewApp,CancellationToken.None,new[]{configs},true);
            Check(leftovers.Count==2&&leftovers.Any(x=>x.Path==exact&&x.Size.Bytes==80&&x.Evidence.Contains("完全匹配"))&&!leftovers.Any(x=>x.Path==similar),"Residual review lists exact-name candidates with evidence and measured size, leaving similar names alone");
            reviewApp.Path=junction;leftovers=ResidualReview.Find(reviewApp,CancellationToken.None,new string[0],true);
            Check(leftovers.Count==0&&File.Exists(Path.Combine(destination,"keep.txt")),"Residual review excludes linked paths and never changes their contents");
            Check(ResidualReview.Probe(new AppEntry(),CancellationToken.None)==RegistrationState.Unregistered&&ResidualReview.Probe(new AppEntry{RegistryPath="invalid"},CancellationToken.None)==RegistrationState.Unknown,"Registration probe distinguishes unregistered and unverified records");
            var missingRecord=new AppEntry{Hive=Microsoft.Win32.RegistryHive.CurrentUser,View=Microsoft.Win32.RegistryView.Registry64,RegistryPath=@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\CleanBox-"+Guid.NewGuid().ToString("N")};
            Check(ResidualReview.Probe(missingRecord,CancellationToken.None)==RegistrationState.Missing,"Single-record follow-up detects an absent registration without changing the registry");
            Reject(()=>Safety.Plan(linkHolder,true),"Deletion preflight rejects a real directory junction");
            Check(File.Exists(Path.Combine(destination,"keep.txt")),"Junction destination was not changed");Directory.Delete(junction,false);
            string csv=Path.Combine(root,"export.csv");Store.Export(new[]{new AppEntry{Name="=1+1"}},csv);Check(File.ReadAllText(csv).Contains("'=1+1"),"CSV formula-like names are neutralized");
            string folder=Path.Combine(root,"nested"),sub=Path.Combine(folder,"child");Directory.CreateDirectory(sub);string f=Path.Combine(sub,"只读文件.txt");File.WriteAllText(f,"test");File.SetAttributes(f,FileAttributes.ReadOnly);
            var plan=Safety.Plan(folder,true);Check(plan.First()==f&&plan.Last()==folder,"Deletion plan orders children before parent");
            var deleted=Operations.Permanent(folder,true);Check(deleted.Failed.Count==0&&!Directory.Exists(folder),"Read-only Unicode fixture deleted successfully");
            string disk=Path.Combine(root,"scan");Directory.CreateDirectory(Path.Combine(disk,"node_modules"));File.WriteAllText(Path.Combine(disk,"portable.exe"),"fixture");File.WriteAllText(Path.Combine(disk,"node_modules","helper.exe"),"fixture");
            var files=Scanner.WalkFiles(disk,"*.exe",CancellationToken.None,null).ToList();Check(files.Count==1&&files[0].EndsWith("portable.exe"),"Portable EXE discovered; dependency directory excluded");
            Check(Scanner.FromExe(files[0],"磁盘发现").SizeBytes==7,"Disk discovery measures the EXE file rather than its directory");
            Check(FileSizes.FromEstimatedKilobytes(2048)==2097152&&FileSizes.FromEstimatedKilobytes(0)==null,"Installed size converts registered KB and distinguishes unknown size");
            var cancel=new CancellationTokenSource();cancel.Cancel();Reject(()=>Scanner.WalkFiles(disk,"*.exe",cancel.Token,null).ToList(),"Scan cancellation honored");cancel.Dispose();
            string locked=Path.Combine(root,"held.txt");File.WriteAllText(locked,"locked fixture");
            using(var helper=Process.Start(new ProcessStartInfo(System.Windows.Forms.Application.ExecutablePath,"--hold-test \""+locked+"\""){UseShellExecute=false,CreateNoWindow=true})){
                try{for(int i=0;i<100&&!File.Exists(locked+".ready");i++)Thread.Sleep(100);Check(File.Exists(locked+".ready"),"Independent lock fixture started");
                    var report=Native.FindLocks(locked,CancellationToken.None);Check(report.Owners.Any(o=>o.Pid==helper.Id),"Restart Manager identifies actual locking process");
                    Check(report.SizeBytes==14&&!report.SizePartial,"File size is available while a process holds an exclusive lock");
                    var fail=Operations.Permanent(locked,true);Check(fail.Failed.Count==1&&File.Exists(locked),"Locked delete reports failure without claiming success");
                }finally{if(!helper.HasExited){helper.Kill();helper.WaitForExit();}}
            }
            VerifyV240(root);
            var clean=Operations.Permanent(root,true);Check(clean.Failed.Count==0&&!Directory.Exists(root),"Only test fixture removed after verification");
            results.Add("No installed software, user data, reboot queue or registry entries were modified.");
            File.WriteAllLines(Path.Combine(Store.Data,"test-results.txt"),results,new UTF8Encoding(true));
        }
        internal static void VerifyV243(string root) {
            string driveRoot=Path.GetPathRoot(root);
            Check(Safety.Normalize(driveRoot)==driveRoot&&Safety.Normalize(driveRoot.Replace('\\','/'))==driveRoot,
                "Drive-root normalization retains the absolute root in both slash styles");
            Check(MainForm.PathArgument(driveRoot)==driveRoot&&MainForm.PathArgument(Path.Combine(root,"With spaces"))=="\""+Path.Combine(root,"With spaces")+"\"",
                "Explorer and relaunch receive an absolute root and quoted paths with spaces");

            string scan=Path.Combine(root,"extension-fixture"), dependencies=Path.Combine(scan,"Node_Modules");
            Directory.CreateDirectory(dependencies);
            File.WriteAllText(Path.Combine(scan,"portable.EXE"),"fixture");
            File.WriteAllText(Path.Combine(scan,"not-an-executable.exex"),"fixture");
            File.WriteAllText(Path.Combine(dependencies,"helper.exe"),"fixture");
            var files=Scanner.WalkFiles(scan,"*.exe",CancellationToken.None,null).Select(Path.GetFileName).ToArray();
            Check(files.SequenceEqual(new[]{"portable.EXE"}),"EXE discovery checks the exact extension and excludes dependencies regardless of case");

            string missing=Path.Combine(root,"already-removed"), uninstaller=Path.Combine(missing,"uninstall.exe");
            var stale = new AppEntry { InstallLocation=missing, Command="\""+uninstaller+"\" /remove" };
            stale.Registration = Scanner.AssessRegistration(stale, "", false);
            Operations.EnsureStaleRegistration(stale, missing);
            Check(true,"A missing verified install location and uninstall executable allow stale cleanup");
            stale.Registration = Scanner.AssessRegistration(stale, Path.Combine(scan,"portable.EXE"), false);
            Reject(()=>Operations.EnsureStaleRegistration(stale,missing),"A present launcher protects an app even when the registered install location is obsolete");
            stale.Registration = Scanner.AssessRegistration(stale, "", true);
            Reject(()=>Operations.EnsureStaleRegistration(stale,missing),"Windows Installer registrations remain protected");
            stale.Registration = Scanner.AssessRegistration(stale, "", false);
            Reject(()=>Operations.EnsureStaleRegistration(stale,scan),"An existing inferred install location protects the registration");
            var unknown=new AppEntry { Registration=RegistryStatus.Incomplete };
            Reject(()=>Operations.EnsureStaleRegistration(unknown,""),"Missing evidence cannot authorize registry cleanup");

            var original=new AppEntry { Name="Removed app", Path=Path.Combine(root,"app") };
            string residual=Path.Combine(root,"settings");
            Check(ResidualReview.UsesResidualLocation(original,residual,new AppEntry { Name="Updated app", Path=Path.Combine(original.Path,"bin") }),
                "A replacement registration using the previous install directory protects its related residual");
            Check(ResidualReview.UsesResidualLocation(original,residual,new AppEntry { Name="Other app", Executable=Path.Combine(residual,"app.exe") }),
                "An active executable inside a residual prevents its recycling");
            Check(ResidualReview.UsesResidualLocation(original,residual,new AppEntry { Name="removed app", Path=Path.Combine(root,"new-app") }),
                "A reinstallation under the same name protects its settings even if its install path changes");
            Check(!ResidualReview.UsesResidualLocation(original,residual,new AppEntry { Name="Other app", Path=original.Path+"-other" }),
                "An unrelated registration with a similar path prefix does not block recycling");
            Reject(()=>ResidualReview.EnsureCanRecycle(new AppEntry { RegistryPath="invalid" },residual,CancellationToken.None),"Recycling refuses a registration that cannot be verified");
        }
        internal static void VerifyV260(string root) {
            string history = Path.Combine(root, "damaged-history");
            Directory.CreateDirectory(history);
            string path = Path.Combine(history, "catalog-state.json");
            const string damaged = "{ damaged!";
            File.WriteAllText(path, damaged);
            var state = new CatalogState(history);
            var app = new AppEntry { Name="Recovery fixture", RegistryPath="recovery", SizeBytes=123 };
            state.Remember(app); state.SaveMeasurement(app);
            Check(new CatalogState(history).Pending.Single().Id == app.Id
                && File.ReadAllText(Directory.GetFiles(history,"catalog-state.corrupt-*.json").Single()) == damaged,
                "Damaged history is backed up and new follow-up and measurement data can be saved");
            var restored = new AppEntry { Name=app.Name, RegistryPath=app.RegistryPath };
            new CatalogState(history).RestoreMeasurement(restored);
            Check(restored.SizeBytes==123,"Measurements survive a restart after history recovery");
            const string newer = "{\"Format\":2,\"Pending\":null}";
            File.WriteAllText(path, newer);
            Reject(()=>state.Remember(app),"A newer history format never triggers damaged-file recovery");
            Check(File.ReadAllText(path)==newer,"An unsupported history version remains intact");
            Check(MainForm.DescribeLocks(new LockReport { Partial=true }).StartsWith("暂无法确认占用"),
                "An incomplete lock check does not look like a confirmed empty result");
        }

        internal static void VerifyUninstallAdvice() {
            var protectedApp = UninstallAdvice.For(new AppEntry { Name="Google Chrome", Publisher="Google LLC", Protected=true });
            Check(protectedApp.Category=="系统标记组件" && protectedApp.Recommendation=="建议保留",
                "Registry protection takes precedence over matching a removable desktop app profile");
            var framework = new AppEntry { Package="fixture_1_x64__publisher", IsPackageFramework=true, Protected=true };
            Check(CatalogClassification.Kind(framework)==2 && UninstallAdvice.For(framework).Category.Contains("共享框架"),
                "Protected package frameworks are identified as shared components");
            var app = new AppEntry { Name="Google Chrome", Publisher="Google LLC", RegistryPath="fixture", Command="uninstall.exe" };
            var browser = UninstallAdvice.For(app);
            Check(browser.Category.Contains("非系统组件") && browser.Recommendation.Contains("考虑卸载") && browser.Impact.Contains("书签"),
                "A known vendor and application receive tailored uninstall impact");
            app.Publisher="另一个发布者";
            Check(UninstallAdvice.For(app).Recommendation=="先核实用途", "A matching name alone does not identify a desktop app");
            app.Registration=RegistryStatus.Stale;
            Check(UninstallAdvice.For(app).Recommendation=="无需卸载", "A stale entry is treated as an uninstall registration");
            Check(UninstallAdvice.For(new AppEntry { Executable="candidate.exe", ReportedDescription="本地文件说明" }).Purpose.Contains("本地文件说明"),
                "File description is labeled and available for an unknown candidate");
            var runtime=UninstallAdvice.For(new AppEntry { Name="Microsoft Edge WebView2 Runtime", RegistryPath="runtime-fixture" });
            Check(runtime.Recommendation=="建议保留" && runtime.Impact.Contains("依赖"), "Shared runtimes explain the impact on other applications");
            var calculator=new AppEntry { Name="Microsoft.WindowsCalculator", Package="Microsoft.WindowsCalculator_1.0.0.0_x64__8wekyb3d8bbwe" };
            Check(UninstallAdvice.For(calculator).Purpose.Contains("单位换算"), "A recognized store package receives a specific description");
            calculator.Package="Microsoft.WindowsCalculator_1.0.0.0_x64__otherpublisher";
            Check(UninstallAdvice.For(calculator).Recommendation=="先核实用途", "An unrecognized store publisher does not receive a generic safe-removal verdict");
        }

        static void VerifyRegistrationVisibility(string root) {
            string folder = Path.Combine(root, "registration-fixture");
            string launcher = Path.Combine(folder, "launcher.exe");
            string uninstaller = Path.Combine(folder, "uninstall_complete.exe");
            var app = new AppEntry { Name = "WeGame", Publisher = "WeGame" };
            Check(Scanner.AssessRegistration(app, "", false) == RegistryStatus.Incomplete,
                "A registration containing only the display name and publisher needs review");
            Check(Scanner.AssessRegistration(app, "", true) == RegistryStatus.Registered,
                "Sparse Windows Installer registrations stay in the installed list");
            Check(Scanner.AssessRegistration(new AppEntry { Version = "1.0" }, "", false) == RegistryStatus.Registered,
                "A versioned registration without local paths is inconclusive");

            Directory.CreateDirectory(folder);
            File.WriteAllText(launcher, "fixture");
            File.WriteAllText(uninstaller, "fixture");
            app.Command = "\"" + uninstaller + "\" /remove";
            Check(Scanner.AssessRegistration(app, launcher, false) == RegistryStatus.Registered,
                "An existing launcher and uninstaller keep the installation visible");
            File.Delete(launcher);
            Check(Scanner.AssessRegistration(app, launcher, false) == RegistryStatus.Registered,
                "An existing uninstaller protects the registration when the icon is missing");
            File.Delete(uninstaller);
            Check(Scanner.AssessRegistration(app, launcher, false) == RegistryStatus.Stale,
                "A WeGame-like key with no install location and two missing executables is stale");
            File.WriteAllText(uninstaller, "fixture");
            app.QuietCommand = "\"" + uninstaller + "\" /quiet";
            app.Command = "";
            Check(Scanner.AssessRegistration(app, launcher, false) == RegistryStatus.Registered,
                "An existing quiet uninstaller protects the registration");
            app.Command = "MsiExec /I{00000000-0000-0000-0000-000000000000}";
            app.QuietCommand = "";
            Check(Scanner.AssessRegistration(app, launcher, false) == RegistryStatus.Registered,
                "A Windows Installer command does not count as missing local evidence");
            Check(Scanner.AssessRegistration(new AppEntry { InstallLocation = @"\\host\share\Game" }, "", false) == RegistryStatus.Registered,
                "An unverified network path does not count as a removed installation");
            Check(!ResidualReview.CanRecycle(RegistrationState.Stale) && !ResidualReview.CanRecycle(RegistrationState.Incomplete),
                "Registry review status never grants permission to recycle residual files");
        }

        internal static void VerifyV244(string root) {
            var state = new CatalogState(null);
            var original = new AppEntry { Name = "Repeat uninstall", Version = "1", RegistryPath = "Repeat-Task", Path = Path.Combine(root, "old-app") };
            var latest = new AppEntry { Name = original.Name, Version = "2", RegistryPath = "repeat-task", Path = Path.Combine(root, "new-app") };
            state.Remember(original);
            state.Remember(latest, replaceExisting: true);
            state.Remember(original);
            Check(state.Pending.Single() == latest, "A repeat uninstall records the new path and a passive recheck cannot restore the old one");

            string executable = Path.Combine(root, "remeasured-portable.exe");
            File.WriteAllBytes(executable, new byte[33]);
            var measured = Scanner.FromExe(executable, "Fixture");
            measured.SizeMeasuredUtc = DateTime.UtcNow;
            state.SaveMeasurement(measured);
            var unchanged = Scanner.FromExe(executable, "Fixture");
            state.RestoreMeasurement(unchanged);
            Check(unchanged.SizeBytes == 33 && unchanged.SizeMeasuredUtc == measured.SizeMeasuredUtc,
                "An unchanged portable executable keeps its manual measurement metadata");
            File.WriteAllBytes(executable, new byte[102]);
            var resized = Scanner.FromExe(executable, "Fixture");
            state.RestoreMeasurement(resized);
            Check(resized.SizeBytes == 102 && !resized.SizeStale && resized.SizeMeasuredUtc == DateTime.MinValue,
                "A fresh portable executable size takes precedence over its saved measurement");
            File.WriteAllBytes(executable, new byte[33]);
            var updated = Scanner.FromExe(executable, "Fixture");
            updated.Version = "updated";
            state.RestoreMeasurement(updated);
            Check(updated.SizeBytes == 33 && !updated.SizeStale && updated.SizeMeasuredUtc == DateTime.MinValue,
                "An updated portable executable can use its verified size even when its length is unchanged");

            string driveRoot = Path.GetPathRoot(root);
            string normalized = Scanner.CleanPath(driveRoot);
            Check(normalized == driveRoot && CatalogClassification.Drive(new AppEntry { Path = normalized }) == driveRoot
                && MainForm.PathArgument(normalized) == driveRoot, "A registered drive root remains absolute through the disk filter and Explorer argument");
            Check(Scanner.CleanPath(root + "\\") == root, "Catalog path cleanup still trims the trailing separator of a normal directory");
        }
        internal static void VerifyV250(string root) {
            string scan = Path.Combine(root, "snapshot-source"), history = Path.Combine(root, "snapshots");
            string growth = Path.Combine(scan, "growing");
            Directory.CreateDirectory(growth);
            string changed = Path.Combine(growth, "changed.bin"), removed = Path.Combine(scan, "removed.bin");
            File.WriteAllBytes(changed, new byte[4]);
            File.WriteAllBytes(removed, new byte[16]);
            var baseline = DiskUsage.Scan(scan, CancellationToken.None, null);
            DiskSnapshot.Save(history, baseline);
            var snapshot = DiskSnapshot.Load(history, scan);
            Check(snapshot.Root.Bytes == 20 && snapshot.Root.ListingComplete && snapshot.ScannedUtc == baseline.ScannedUtc,
                "A disk snapshot retains its tree, totals and scan time on disk");
            File.WriteAllBytes(changed, new byte[32]);
            File.Delete(removed);
            File.WriteAllBytes(Path.Combine(scan, "new.bin"), new byte[9]);
            var current = DiskUsage.Scan(scan, CancellationToken.None, null);
            var changes = new UsageChange(snapshot.Root, current.Root).Children();
            Check(changes.Select(row => row.Delta).SequenceEqual(new long?[] { 28, 9, -16 })
                && changes[0].Children().Single().Delta == 28,
                "Disk comparison sorts changes and can drill into growing folders");
            var partial = DiskUsage.Scan(scan, CancellationToken.None, null, 1);
            Check(new UsageChange(snapshot.Root, partial.Root).Children().All(row => !row.Delta.HasValue),
                "Entries missing from an incomplete listing do not appear as removals");
            Reject(() => DiskSnapshot.Save(history, partial), "An entry-limited scan cannot replace the baseline");
            DiskSnapshot.Save(history, current);
            Check(DiskSnapshot.Load(history, scan).Root.Bytes == 41, "Saving a new scan replaces the baseline for that exact path");

            string cleanup = Path.Combine(root, "cleanup-preview"), nested = Path.Combine(cleanup, "nested");
            Directory.CreateDirectory(nested);
            string sample = Path.Combine(nested, "sample.bin");
            File.WriteAllBytes(sample, new byte[13]);
            var plan = CleanupPlan.Capture(cleanup, CancellationToken.None, true);
            Check(plan.Files == 1 && plan.Directories == 2 && plan.Bytes == 13 && plan.Preview.Contains(cleanup),
                "Cleanup preview includes the full path, file and directory counts, and total bytes");
            File.WriteAllBytes(sample, new byte[17]);
            Reject(() => Operations.Permanent(plan), "The approved cleanup stops when the target changes");
            var deletion = Operations.Permanent(CleanupPlan.Capture(cleanup, CancellationToken.None, true));
            Check(deletion.Done == 3 && deletion.Failed.Count == 0 && File.ReadAllText(deletion.ReportPath).Contains(sample) && !Directory.Exists(cleanup),
                "Cleanup stores a per-item report for the approved fixture");
            Check(DeleteResult.Reason(new System.ComponentModel.Win32Exception(32)).StartsWith("文件被占用")
                && DeleteResult.Reason(new System.ComponentModel.Win32Exception(5)).StartsWith("权限不足"),
                "Cleanup errors separate sharing violations from permission errors");

            var sizes = Native.FindLocks(changed, CancellationToken.None);
            Check(sizes.SizeBytes == 32 && sizes.Files == 1, "The lock page keeps the measured file size when Windows occupancy detection is unavailable");
        }
        public static void ScanReport(){Directory.CreateDirectory(Store.Data);var apps=new List<AppEntry>();var messages=new List<string>();
            try{Scanner.Registered(apps.Add,messages.Add,CancellationToken.None);Scanner.Packages(apps.Add,messages.Add,CancellationToken.None);Scanner.Shortcuts(apps.Add,messages.Add,CancellationToken.None);
                Store.Export(apps,Path.Combine(Store.Data,"initial-scan.csv"));messages.Add("Registry entries: "+apps.Count(a=>a.RegistryPath.Length>0));messages.Add("Appx entries: "+apps.Count(a=>a.Package.Length>0));messages.Add("Shortcut candidates: "+apps.Count(a=>a.Source.StartsWith("快捷")));messages.Add("Read-only scan completed.");
                File.WriteAllLines(Path.Combine(Store.Data,"scan-results.txt"),messages,new UTF8Encoding(true));
            }catch(Exception e){File.WriteAllText(Path.Combine(Store.Data,"scan-results.txt"),"FAIL\n"+e);Environment.ExitCode=1;}
        }
    }
}
