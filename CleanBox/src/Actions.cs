using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CleanBox {
    public partial class MainForm {
        bool Idle(){if(!working)return true;MessageBox.Show(this,"请先等待当前操作完成，或取消扫描。","正在处理");return false;}
        void Busy(bool value,bool cancellable=false){working=value;quick.Enabled=disk.Enabled=folder.Enabled=!value;cancel.Enabled=value&&cancellable;cancel.Visible=value&&cancellable;progress.Visible=value;adminButton.Enabled=!value&&!Native.IsAdmin;UpdateFileActions();UpdateUsageActions();ShowSelection();UpdateCounts();}
        async Task RunScan(Action<CancellationToken> action,string finished){if(!Idle())return;cts=new CancellationTokenSource();Busy(true,true);try{await Task.Run(()=>action(cts.Token));DrainAll();latestStatus=finished+" · "+apps.Count+" 条";Store.Log(latestStatus);}catch(OperationCanceledException){DrainAll();latestStatus="已停止，保留已发现的 "+apps.Count+" 条结果。";}catch(Exception e){ShowError(e);}finally{Busy(false);cts.Dispose();cts=null;}}
        async Task QuickScan() {
            if (!Idle()) return;
            var previous = apps.ToArray();
            cts = new CancellationTokenSource();
            var token = cts.Token;
            Busy(true, true);
            try {
                var fresh = await Task.Run(() => {
                    var entries = new List<AppEntry>();
                    Scanner.Registered(entries.Add, s => latestStatus = s, token);
                    try { Scanner.Packages(entries.Add, s => latestStatus = s, token); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) {
                        entries.AddRange(previous.Where(app => app.Package.Length > 0));
                        Store.Log("商店应用未能完整读取：" + ex.Message);
                    }
                    Scanner.Shortcuts(entries.Add, s => latestStatus = s, token);
                    foreach (var app in previous.Where(app => app.Source.StartsWith("磁盘", StringComparison.Ordinal))) {
                        token.ThrowIfCancellationRequested();
                        if (!Scanner.IsMissingLocalPath(app.Executable)) entries.Add(app);
                    }
                    return entries;
                });
                token.ThrowIfCancellationRequested();
                ReplaceCatalog(fresh);
                latestStatus = "基础扫描完成 · " + apps.Count + " 条";
                Store.Log(latestStatus);
            } catch (OperationCanceledException) { latestStatus = "已取消刷新，保留上次清单。"; }
            catch (Exception ex) { ShowError(ex); }
            finally { Busy(false); cts.Dispose(); cts = null; }
        }
        async Task ChooseFolder(){if(!Idle())return;using(var f=new FolderBrowserDialog{Description="选择软件存放目录，将递归发现 EXE 程序（只读扫描）",SelectedPath=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),ShowNewFolderButton=false})if(f.ShowDialog(this)==DialogResult.OK){string root=f.SelectedPath;await RunScan(t=>Scanner.Disk(root,queue.Enqueue,s=>latestStatus=s,t),"目录扫描完成");}}
        async Task ChooseDrives(){if(!Idle())return;using(var f=new Form{Text="选择磁盘扫描范围",ClientSize=new Size(540,350),StartPosition=FormStartPosition.CenterParent,Font=Font,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false}){
            var list=new CheckedListBox{Dock=DockStyle.Fill,CheckOnClick=true};foreach(var drive in DriveInfo.GetDrives().Where(d=>d.IsReady&&d.DriveType==DriveType.Fixed))list.Items.Add(drive.RootDirectory.FullName,true);
            var info=new Label{Dock=DockStyle.Top,Height=118,Padding=new Padding(15),Text="递归查找所选磁盘中的 EXE，补充未登记的软件。\r\n可能需要几分钟，可随时取消并保留结果。\r\n跳过系统保护区、无权限目录、链接及常见开发依赖目录；发现项需人工核对，不保证百分之百识别。"};
            var go=new Button{Text="开始只读扫描",Dock=DockStyle.Bottom,Height=44,DialogResult=DialogResult.OK};f.Controls.Add(list);f.Controls.Add(info);f.Controls.Add(go);ApplyTheme(f);if(f.ShowDialog(this)!=DialogResult.OK)return;string[] roots=list.CheckedItems.Cast<string>().ToArray();if(roots.Length==0)return;await RunScan(t=>{foreach(string root in roots)Scanner.Disk(root,queue.Enqueue,s=>latestStatus=s,t);},"磁盘扫描完成（跳过项和限制请见说明）");}}
        void OpenSelected(){var a=Selected();if(a!=null)OpenPath(a.Path.Length>0?a.Path:a.Executable);}
        void OpenPath(string path) {
            try {
                bool isFile = File.Exists(path);
                if (!isFile && !Directory.Exists(path)) {
                    MessageBox.Show(this,"路径不存在或未提供。","打开位置");
                    return;
                }
                string target = path.TrimEnd('\\','/');
                if (target.Length == 2 && target[1] == ':' && path.Length > 2) target += "\\";
                string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe");
                Process.Start(new ProcessStartInfo(explorer,(isFile ? "/select," : "") + PathArgument(target)) { UseShellExecute=true });
            } catch(Exception e) { ShowError(e); }
        }
        internal static string PathArgument(string path) {
            return path.Length == 3 && path[1] == ':' && path[2] == '\\' ? path : "\"" + path + "\"";
        }
        void UseSelectedFolder(){var a=Selected();if(a==null)return;if(a.Path.Length==0){MessageBox.Show(this,"该条目没有提供安装目录，请手动选择目标。","清理匣");return;}pathBox.Text=a.Path;SwitchPage(1);}
        async Task UninstallSelected(){if(!Idle())return;var a=Selected();if(a==null||a.NeedsRegistrationReview)return;if(a.Protected){MessageBox.Show(this,"此项为受保护组件。","受保护项目");return;}
            var advice=UninstallAdvice.For(a);
            string target=a.Package.Length>0?"当前用户的软件包\r\n"+a.Package:"将启动登记中的卸载程序\r\n"+a.Command;
            if(!Confirm("正常卸载",a.Name+"\r\n"+advice.Category+" · "+advice.Recommendation+"\r\n\r\n卸载影响\r\n"+advice.Impact+"\r\n\r\n"+target,false))return;
            Busy(true);bool launched=false;try{await Task.Run(()=>Operations.Uninstall(a));launched=true;RememberUninstall(a,replaceExisting:true);latestStatus="卸载已启动，完成后请核对结果与残留。";}catch(Exception e){ShowError(e);}finally{Busy(false);}if(launched)ShowResidualReview(a,true);}
        async Task ClearRecord(){if(!Idle())return;var a=Selected();if(a==null||a.Registration!=RegistryStatus.Stale)return;if(!Confirm("清除失效记录",a.Name+"\r\n"+a.Hive+"\\"+a.RegistryPath+"\r\n\r\n清理这条失效的卸载登记前会导出备份。",true))return;
            Busy(true);try{await Task.Run(()=>Operations.RemoveStaleRecord(a));if(UpdateCatalogRegistration(a,RegistrationState.Missing)){RebuildCatalogIndex();ApplyFilter();}MessageBox.Show(this,"失效记录已删除，软件清单已更新。备份保存在 data 目录。","完成");}catch(Exception e){ShowError(e);}finally{Busy(false);}}
        void Export(){ApplyFilter();using(var f=new SaveFileDialog{Title="导出当前筛选结果",Filter="CSV 表格|*.csv",FileName="软件清单-"+DateTime.Now.ToString("yyyyMMdd")+".csv",InitialDirectory=Store.Home})if(f.ShowDialog(this)==DialogResult.OK)try{Store.Export(grid.Rows.Cast<DataGridViewRow>().Where(r=>r.Visible).Select(r=>(AppEntry)r.Tag),f.FileName);latestStatus="清单已导出："+f.FileName;}catch(Exception e){ShowError(e);}}
        void ResidualDialog(){if(!Idle())return;var app=Selected();if(app!=null&&!app.Protected)ShowResidualReview(app,false);}
        async Task Inspect(){
            if(!Idle())return;
            string p=pathBox.Text;
            cts=new CancellationTokenSource();Busy(true,true);lockList.Items.Clear();lockPath="";SetStep(1);
            lockSize.Text="目标大小：正在读取…";
            lockSummary.Text="正在检查文件与进程…";latestStatus="正在检查占用，可随时取消";
            try {
                var report=await Task.Run(()=>Native.FindLocks(p,cts.Token));
                if(pathBox.Text!=p){lockSize.Text="目标大小：—";lockSummary.Text="目标已改变，请重新检查。";return;}
                lockPath=Safety.Normalize(p);
                string size=report.SizeBytes.HasValue?FileSizes.Format(report.SizeBytes.Value):"未能读取";
                if(report.SizePartial&&report.SizeBytes.HasValue)size="至少 "+size+"（部分文件未统计）";
                lockSize.Text="目标大小："+size;
                foreach(var o in report.Owners)lockList.Items.Add(o);
                if(lockList.Items.Count>0)lockList.SelectedIndex=0;
                lockSummary.Text=DescribeLocks(report);
                tips.SetToolTip(lockSummary,lockSummary.Text);
                SetStep(report.Partial||report.Owners.Count>0?1:2);
                latestStatus=report.Partial?"占用检查部分完成，详情见当前页面":"占用检查完成";
                Store.Log(latestStatus+"："+lockPath+"；相关进程 "+report.Owners.Count+(report.Note.Length>0?"\n"+report.Note:""));
            } catch(OperationCanceledException) {
                lockSize.Text="目标大小：—";lockSummary.Text="已取消检查，请重新检查后再处理。";latestStatus="占用检查已取消";SetStep(0);
            } catch(Exception e) {
                lockSize.Text="目标大小：未能读取";ShowError(e);lockSummary.Text="检查失败，请查看操作记录。";SetStep(0);
            } finally {Busy(false);cts.Dispose();cts=null;}
        }
        internal static string DescribeLocks(LockReport report) {
            int confirmed=report.Owners.Count(owner=>owner.Confirmed);
            string title=report.Partial?(confirmed==0?"暂无法确认占用":"已确认部分占用")
                :(confirmed==0?"未发现已确认的占用":"发现文件占用");
            return title+" · 检查 "+report.Files+" 个文件 · 确认 "+confirmed+" · 线索 "+(report.Owners.Count-confirmed)
                +"\r\n"+(report.Note.Length>0?report.Note:"从目标运行的进程仅供排查。部分文件夹句柄、驱动和权限限制可能影响检测。");
        }
        async Task StopOwner(bool force){if(!Idle())return;var owner=lockList.SelectedItem as LockOwner;if(owner==null)return;if(!owner.CanStop){MessageBox.Show(this,"该进程属于系统或无法核实身份。可手动退出软件，或对具体文件选择重启后删除。","不能结束此进程");return;}
            if(!Confirm(force?"强制结束进程":"请求正常退出",owner.Display+"\r\n"+owner.Path+(force?"\r\n\r\n该程序尚未保存的内容会丢失。":"\r\n\r\n程序可能会弹出保存提示。"),force))return;
            try{MessageBox.Show(this,Native.Stop(owner,force),"处理结果");await Inspect();}catch(Exception e){ShowError(e);}}
        async Task DeleteTarget(int mode) {
            if (!Idle()) return;
            string target = pathBox.Text;
            CleanupPlan plan;
            cts = new CancellationTokenSource(); Busy(true, true);
            latestStatus = "正在生成清理预览…";
            try { plan = await Task.Run(() => CleanupPlan.Capture(target, cts.Token)); }
            catch (OperationCanceledException) { latestStatus = "已取消生成清理预览。"; return; }
            catch (Exception ex) { ShowError(ex); return; }
            finally { Busy(false); cts.Dispose(); cts = null; }

            string title = mode == 0 ? "移到回收站" : mode == 1 ? "永久删除" : "登记重启后删除";
            string explanation = mode == 0 ? "将目标及其全部内容交给 Windows 回收站处理。若系统提示无法回收，请取消。"
                : mode == 1 ? "目标及其全部内容将被永久删除。"
                : "已登记的路径将在下次系统重启时尝试永久删除。请在重启后核对结果。";
            latestStatus = "清理预览已生成。";
            if (!Confirm(title, plan.Preview + "\r\n\r\n" + explanation + "\r\n\r\n请核对目标与处理范围。", mode != 0, plan)) {
                latestStatus = "已取消清理。"; return;
            }
            Busy(true); latestStatus = "正在" + title + "…";
            try {
                DeleteResult result = mode == 0 ? await RecycleInBackground(plan)
                    : await Task.Run(() => mode == 1 ? Operations.Permanent(plan) : Operations.Reboot(plan));
                if (mode != 2 && (result.Done > 0 || result.Skipped > 0)) {
                    try { await RefreshCatalogAfterCleanup(plan.Root); }
                    catch (Exception ex) { Store.Log("软件清单刷新失败，可按 F5 重新扫描。" + ex.Message); }
                }
                ShowCleanupResult(this, result, mode == 2);
                latestStatus = result.Summary;
                lockList.Items.Clear(); lockPath = ""; SetStep(2); lockSize.Text = "目标大小：—";
                lockSummary.Text = result.Summary + "\r\n逐项结果见操作记录。";
            } catch (Exception ex) { ShowError(new IOException(DeleteResult.Reason(ex), ex)); }
            finally { Busy(false); }
        }

        async Task RefreshCatalogAfterCleanup(string target) {
            var affected = apps.Where(app => app.Package.Length == 0 && AffectedByCleanup(app, target)).ToArray();
            if (affected.Length == 0) return;
            var states = await Task.Run(() => affected.Select(app => {
                if (!IsCandidate(app)) return ResidualReview.Probe(app, CancellationToken.None);
                return Scanner.IsMissingLocalPath(app.Executable) ? RegistrationState.Missing : RegistrationState.Unregistered;
            }).ToArray());
            bool changed = false;
            for (int i = 0; i < affected.Length; i++) changed |= UpdateCatalogRegistration(affected[i], states[i]);
            if (changed) { RebuildCatalogIndex(); ApplyFilter(); }
        }

        static bool AffectedByCleanup(AppEntry app, string target) {
            if (Safety.Within(app.Path, target) || Safety.Within(app.Executable, target)) return true;
            foreach (string command in new[] { app.Command, app.QuietCommand, app.ModifyCommand }) {
                string exe, args;
                if (Scanner.ParseCommand(command, out exe, out args) && Safety.Within(Scanner.CleanPath(exe), target)) return true;
            }
            return false;
        }

        void ShowCleanupResult(IWin32Window owner, DeleteResult result, bool reboot = false) {
            using (var dialog = new Form { Text = "清理结果", Font = Font, ClientSize = new Size(680, 330),
                StartPosition = FormStartPosition.CenterParent, AutoScaleMode = AutoScaleMode.None, Padding = new Padding(16) }) {
                var details = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical,
                    Text = result.Summary + (result.Canceled ? "\r\n已取消回收。" : "")
                        + (reboot ? "\r\n请在重启后核对已登记项目的实际处理结果。" : "")
                        + (result.Failed.Count > 0 ? "\r\n\r\n" + string.Join("\r\n", result.Failed.Take(6)) : "")
                        + (result.ReportError.Length > 0 ? "\r\n\r\n逐项记录未保存：" + result.ReportError : "") };
                var bar = Bar(48); bar.Dock = DockStyle.Bottom;
                var close = Button("关闭", () => dialog.Close());
                close.DialogResult = DialogResult.Cancel; dialog.CancelButton = close;
                bar.Controls.Add(close);
                if (result.ReportPath.Length > 0) bar.Controls.Add(Button("打开逐项记录", () => {
                    try { Process.Start(new ProcessStartInfo(result.ReportPath) { UseShellExecute = true }); }
                    catch (Exception ex) { MessageBox.Show(dialog,ex.Message,"无法打开逐项记录"); }
                }));
                dialog.Controls.Add(details); dialog.Controls.Add(bar);
                ApplyTheme(dialog);
                if (dpiScale != 1) dialog.Scale(new SizeF(dpiScale,dpiScale));
                dialog.ShowDialog(owner);
            }
        }
        bool Confirm(string title,string body,bool typeWord,CleanupPlan plan=null){using(var f=BuildConfirmation(title,body,typeWord,plan))return f.ShowDialog(this)==DialogResult.OK;}
        Form BuildConfirmation(string title,string body,bool typeWord,CleanupPlan plan=null){
            var f=new Form{Text=title,ClientSize=new Size(640,typeWord?430:360),StartPosition=FormStartPosition.CenterParent,Font=Font,AutoScaleMode=AutoScaleMode.None,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,Padding=new Padding(22)};f.SuspendLayout();
            var heading=new Label{Text=title,Dock=DockStyle.Top,Height=48,Font=new Font(Font.FontFamily,16,FontStyle.Bold),AutoEllipsis=true};
            var target=new TextBox{Name="confirmationTarget",Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Text=body,BorderStyle=BorderStyle.None};
            var bottom=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=50,Padding=new Padding(0,10,0,0),FlowDirection=FlowDirection.RightToLeft};
            var yes=new UiButton{Name="confirmationAccept",Text="确认继续",Kind=typeWord?"danger":"primary",Width=122,Height=36,DialogResult=DialogResult.OK,Enabled=!typeWord,Margin=new Padding(8,0,0,0)};
            var no=new UiButton{Name="confirmationCancel",Text="取消",Width=100,Height=36,DialogResult=DialogResult.Cancel,Margin=new Padding(8,0,0,0)};bottom.Controls.Add(no);bottom.Controls.Add(yes);f.CancelButton=no;
            f.Controls.Add(target);
            if(plan!=null){f.ClientSize=new Size(720,f.ClientSize.Height+38);f.Controls.Add(BuildCleanupContents(f,plan));}
            if(typeWord){var entry=new Panel{Dock=DockStyle.Bottom,Height=64,Padding=new Padding(0,5,0,0)};var label=new Label{Text="输入“确认”以继续",Dock=DockStyle.Top,Height=27,Tag="muted"};var word=new TextBox{Name="confirmationWord",Dock=DockStyle.Top};word.TextChanged+=(s,e)=>yes.Enabled=word.Text=="确认";entry.Controls.Add(word);entry.Controls.Add(label);f.Controls.Add(entry);}
            f.Controls.Add(bottom);f.Controls.Add(heading);ApplyTheme(f);if(dpiScale!=1)f.Scale(new SizeF(dpiScale,dpiScale));f.ResumeLayout(true);f.Shown+=(s,e)=>no.Focus();return f;
        }
        void Elevate(){if(!Idle())return;try{string target=string.IsNullOrWhiteSpace(pathBox.Text)?"":Safety.Normalize(pathBox.Text);Process.Start(new ProcessStartInfo(Application.ExecutablePath,target.Length>0?"--target "+PathArgument(target):""){UseShellExecute=true,Verb="runas",WorkingDirectory=Store.Home});Close();}catch(Exception e){ShowError(e);}}
        void ShowError(Exception e){latestStatus=e.Message;Store.Log("操作未完成："+e.Message);MessageBox.Show(this,e.Message,"操作未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
    }
}
