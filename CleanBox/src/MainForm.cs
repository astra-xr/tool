using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace CleanBox {
    public partial class MainForm:Form {
        const int RegistryReviewFilter = 6;
        internal readonly GlassScene Scene=new GlassScene();
        static Color Ink{get{return Palette.Ink;}}static Color Muted{get{return Palette.Muted;}}static Color Teal{get{return Palette.Accent;}}static Color Bg{get{return Palette.Bg;}}static Color Line{get{return Palette.Line;}}
        readonly CatalogState catalogState;
        readonly List<AppEntry> apps=new List<AppEntry>();readonly HashSet<string> ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly ConcurrentQueue<AppEntry> queue=new ConcurrentQueue<AppEntry>();readonly ConcurrentQueue<string> logs=new ConcurrentQueue<string>();
        readonly Dictionary<string,Image> appIcons=new Dictionary<string,Image>();readonly HashSet<string> iconPending=new HashSet<string>();int activeIcons;
        readonly List<Button> selectionActions=new List<Button>();readonly ToolTip tips=new ToolTip{AutoPopDelay=15000,InitialDelay=450};
        DataGridView grid;TextBox search,detail,pathBox,logBox;ChoiceButton filter;Label status,totalLabel,installedLabel,foundLabel,lockSummary,lockSize,detailTitle,detailSubtitle,listCount,emptyList,stepLabel,dropText;
        DataGridViewColumn sizeColumn;
        Label detailSize, detailPublisher, detailVersion, detailSource;
        Label[] steps;ListBox lockList;Button quick,disk,folder,cancel,adminButton,uninstallButton,openButton,residualButton,inspectButton,stopButton,recycleButton,fileMore,themeButton,densityButton,glassButton,motionButton;
        GlassProgress progress;Panel host,sidebar,catalogBody,detailPanel,dropZone;Panel[] pages;UiButton[] navigation;PictureBox detailIcon;ContextMenuStrip scanMenu,appMenu,fileMenu;
        CancellationTokenSource cts;bool working,compact,closing,loadingPreferences=true;volatile string latestStatus="准备就绪";System.Windows.Forms.Timer timer;string lockPath="";int currentPage;float dpiScale=1;
        bool IsCandidate(AppEntry a){return a!=null&&a.RegistryPath.Length==0&&a.Package.Length==0;}
        int Px(int n){return (int)Math.Round(n*dpiScale);}
        public MainForm(string initialTarget=null,bool uiCheck=false,bool settingsPerformance=false){
            catalogState=new CatalogState(uiCheck||settingsPerformance?null:Store.Data);
            SuspendLayout();DoubleBuffered=true;ReadPreferences();Text="清理匣 CleanBox "+typeof(MainForm).Assembly.GetName().Version.ToString(3);Font=new Font("Microsoft YaHei UI",9F);AutoScaleMode=AutoScaleMode.None;ClientSize=new Size(1220,780);MinimumSize=new Size(960,640);StartPosition=FormStartPosition.CenterScreen;BackColor=Bg;ForeColor=Ink;
            try{Icon=new Icon(Path.Combine(Store.Home,"assets","cleanbox.ico"));}catch{}
            BuildShell();Resize+=(s,e)=>progress.RefreshMotion();using(var graphics=CreateGraphics())dpiScale=graphics.DpiX/96f;if(dpiScale!=1f)Scale(new SizeF(dpiScale,dpiScale));MinimumSize=new Size(Px(960),Px(640));ClientSize=new Size(Px(1220),Px(780));ResumeLayout(true);var area=Screen.FromControl(this).WorkingArea;Size=new Size(Math.Min(Width,area.Width-32),Math.Min(Height,area.Height-32));ApplyTheme(this);StyleGrid();StyleUsageGrid();UpdateDensity();SwitchPage(0);ShowSelection();SetStep(0);loadingPreferences=false;
            timer=new System.Windows.Forms.Timer{Interval=200};timer.Tick+=(s,e)=>Drain();timer.Start();Store.Logged+=OnLog;
            Shown+=async(s,e)=>{using(var g=CreateGraphics())dpiScale=g.DpiX/96f;ArrangeCatalog();UpdateDensity();if(settingsPerformance){try{await MeasureSettingsPerformance();}catch(Exception ex){Directory.CreateDirectory(Store.Data);File.WriteAllText(Path.Combine(Store.Data,"settings-performance-error.txt"),ex.ToString());Environment.ExitCode=1;}Close();return;}if(!string.IsNullOrEmpty(initialTarget)){pathBox.Text=initialTarget;SwitchPage(1);}await QuickScan();if(uiCheck){try{await Task.Delay(700);await VerifyInterface();}catch(Exception ex){Directory.CreateDirectory(Store.Data);File.WriteAllText(Path.Combine(Store.Data,"ui-results.txt"),"FAIL\r\n"+ex);Environment.ExitCode=1;}Close();}};
            FormClosing+=(s,e)=>{if(working){MessageBox.Show(this,"请先取消扫描，或等待当前操作完成，再退出。","任务仍在进行");e.Cancel=true;}};
            FormClosed+=(s,e)=>{closing=true;timer.Stop();timer.Dispose();if(searchDelay!=null)searchDelay.Dispose();MotionClock.FinishAll();Scene.Dispose();Store.Logged-=OnLog;tips.Dispose();if(cts!=null)cts.Dispose();foreach(var img in appIcons.Values)if(img!=null)img.Dispose();scanMenu.Dispose();appMenu.Dispose();fileMenu.Dispose();disk.Dispose();folder.Dispose();};
            KeyPreview=true;KeyDown+=async(s,e)=>{if(e.Control&&e.KeyCode==Keys.F){SwitchPage(0);search.Focus();e.SuppressKeyPress=true;}if(e.KeyCode==Keys.F5&&!working){e.SuppressKeyPress=true;if(currentPage==4)await AnalyzeUsage();else await QuickScan();}if(e.KeyCode==Keys.Escape&&cts!=null){cts.Cancel();e.SuppressKeyPress=true;}};
        }
        protected override void OnPaintBackground(PaintEventArgs e){if(Scene==null)base.OnPaintBackground(e);else Scene.Draw(e.Graphics,ClientSize,Point.Empty,false);}
        void SwitchPage(int index){if(pages==null)return;host.SuspendLayout();try{currentPage=index;pages[index].Visible=true;pages[index].BringToFront();for(int i=0;i<pages.Length;i++)if(i!=index)pages[i].Visible=false;for(int i=0;i<navigation.Length;i++)navigation[i].Selected=i==index;settingsButton.Selected=index==3;usageNavigation.Selected=index==4;if(index==0)ArrangeCatalog();}finally{host.ResumeLayout(true);}}
        void ArrangeCatalog(){
            if(catalogBody==null||detailPanel==null)return;
            detailPanel.Width=Px(catalogBody.Width<Px(880)?265:292);
            if(sizeColumn!=null){
                grid.Columns[1].Visible=catalogBody.Width>=Px(810);
                sizeColumn.MinimumWidth=Px(108);
            }
        }
        void SetStep(int step){if(steps==null)return;for(int i=0;i<steps.Length;i++){steps[i].ForeColor=i==step?Teal:Muted;}}
        void UpdateFileActions(){if(inspectButton==null||pathBox==null||stopButton==null)return;bool target=!string.IsNullOrWhiteSpace(pathBox.Text);var owner=lockList.SelectedItem as LockOwner;inspectButton.Enabled=target&&!working;stopButton.Enabled=!working&&owner!=null&&owner.CanStop&&lockPath.Length>0;recycleButton.Enabled=target&&!working;fileMore.Enabled=target&&!working;pathBox.ReadOnly=working;if(fileMenu!=null)fileMenu.Items[0].Enabled=stopButton.Enabled;}
        void OnLog(string s){logs.Enqueue(s);}
        void Drain(){
            if(!queue.IsEmpty){
                var rows=new List<DataGridViewRow>();var matches=CaptureCatalogFilter();AppEntry a;
                for(int n=0;n<200&&queue.TryDequeue(out a);n++){
                    var row=CreateCatalogRow(a,matches);
                    if(row!=null)rows.Add(row);
                }
                if(rows.Count>0){
                    EditCatalogGrid(()=>grid.Rows.AddRange(rows.ToArray()));
                    ShowSelection();UpdateCounts();
                }
            }
            if(status.Text!=latestStatus){status.Text=latestStatus;tips.SetToolTip(status,latestStatus);}
            string l;while(logs.TryDequeue(out l)){if(logBox.TextLength>150000)logBox.Text=logBox.Text.Substring(logBox.TextLength-100000);logBox.AppendText(l+Environment.NewLine);}
        }
        void DrainAll(){while(!queue.IsEmpty)Drain();if(grid.SortedColumn!=null)grid.Sort(grid.SortedColumn,grid.SortOrder==SortOrder.Descending?System.ComponentModel.ListSortDirection.Descending:System.ComponentModel.ListSortDirection.Ascending);ApplyFilter();if(advancedFilters.Visible)RefreshFilterChoices();}
        void UpdateCounts(){int review=apps.Count(a=>a.NeedsRegistrationReview), total=apps.Count-review;
            totalLabel.Text="全部 "+total;installedLabel.Text="已登记 "+apps.Count(x=>!x.NeedsRegistrationReview&&(x.RegistryPath.Length>0||x.Package.Length>0));foundLabel.Text="待核对 "+apps.Count(IsCandidate);
            int count=grid.Rows.GetRowCount(DataGridViewElementStates.Visible);listCount.Text="显示 "+count+" / "+(filter.SelectedIndex==RegistryReviewFilter?review:total)+" 项  ·  双击打开位置";emptyList.Visible=count==0;emptyList.Text=working?"正在读取软件清单…":"没有匹配的软件\r\n可更换关键词、来源筛选或重新扫描";
            Control front=count==0?(Control)emptyList:grid;if(front.Parent.Controls.GetChildIndex(front)!=0)front.BringToFront();}
        DataGridViewRow CreateCatalogRow(AppEntry a, Func<AppEntry,bool> matches) {
            if(!AcceptCatalogEntry(a))return null;
            catalogState.RestoreMeasurement(a); IndexCatalogEntry(a);
            ids.Add(a.Id); apps.Add(a);
            a.SearchText = string.Join(" ", a.Name, a.Publisher, a.Path, a.Executable);
            var row = new DataGridViewRow(); row.CreateCells(grid, a.Name, a.Version, CatalogSource(a), a.SizeBytes);
            row.Tag=a;row.Height=Px(compact?42:48);row.Cells[0].ToolTipText=a.Name+"\r\n"+a.Publisher;row.Cells[2].ToolTipText=a.DisplaySource;
            row.Cells[sizeColumn.Index].ToolTipText=a.SizeBadge+"\r\n"+a.SizeExplanation;
            row.Visible=matches(a);
            return row;
        }
        void Add(AppEntry a) {
            var row = CreateCatalogRow(a, CaptureCatalogFilter());
            if (row != null) grid.Rows.Add(row);
        }
        string CatalogSource(AppEntry a){return a.NeedsRegistrationReview?a.RegistrationLabel:a.Protected?"系统组件":IsCandidate(a)?"绿色候选":a.Package.Length>0?"应用商店":"已安装";}
        Func<AppEntry,bool> CaptureCatalogFilter() {
            string term = appliedSearch;
            int category = filter.SelectedIndex;
            var advanced = CaptureAdvancedFilter();
            return a => {
                if (a == null || a.NeedsRegistrationReview != (category == RegistryReviewFilter)) return false;
                bool type = category == RegistryReviewFilter || category == 0 || (category == 1 && a.RegistryPath.Length > 0)
                    || (category == 2 && a.Package.Length > 0) || (category == 3 && IsCandidate(a))
                    || (category == 4 && a.Source.StartsWith("快捷")) || (category == 5 && a.Source.StartsWith("磁盘"));
                return type && (term.Length == 0 || a.SearchText.IndexOf(term,StringComparison.OrdinalIgnoreCase)>=0) && advanced(a);
            };
        }
        AppEntry Selected(){return grid.SelectedRows.Count>0&&grid.SelectedRows[0].Visible?grid.SelectedRows[0].Tag as AppEntry:null;}
        void ShowSelection(){if(detail==null||changingCatalog)return;var a=Selected();foreach(var b in selectionActions)b.Enabled=a!=null&&!working;
            if(a==null){detailTitle.Text="选择软件";detailSubtitle.Text="";detailIcon.Image=null;
                detailSize.Text=detailPublisher.Text=detailVersion.Text=detailSource.Text="—";
                detail.Text="选择软件后可查看安装位置和卸载入口。";return;}
            bool candidate=IsCandidate(a);detailTitle.Text=a.Name;tips.SetToolTip(detailTitle,a.Name);detailSubtitle.Text=a.Protected?"系统组件 · 已保护":candidate?"绿色软件候选 · 需要核对":CatalogSource(a);
            string size=a.SizeBytes.HasValue?a.SizeSource+"\r\n":"";
            if(a.SizeKind==AppSizeKind.EngineLauncher)size+="引擎内的应用本体和缓存未计入。\r\n";
            size+=a.SizeExplanation;if(a.SizeMeasuredUtc!=DateTime.MinValue)size+="\r\n实测时间："+a.SizeMeasuredUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            detailSize.Text=a.SizeBytes.HasValue?(a.SizePartial?"≥ ":"")+FileSizes.Format(a.SizeBytes.Value):"大小未读取";
            detailPublisher.Text=a.Publisher.Length>0?a.Publisher:"未提供";
            detailVersion.Text=a.Version.Length>0?a.Version:"未提供";
            detailSource.Text=a.DisplaySource;
            foreach (var label in new[] { detailPublisher, detailVersion, detailSource }) tips.SetToolTip(label,label.Text);
            detail.Text=size+"\r\n\r\n安装位置\r\n"+(a.Path.Length>0?a.Path:"未提供")+"\r\n\r\n"+(a.Package.Length>0?"软件包\r\n"+a.Package:"卸载入口\r\n"+(a.Command.Length>0?a.Command:"未提供"));
            uninstallButton.Text=a.Protected?"系统组件 · 已保护":candidate?"核对并处理目录":"正常卸载";uninstallButton.Enabled=!working&&!a.Protected&&!a.NeedsRegistrationReview&&(candidate?a.Path.Length>0:a.Command.Length>0||a.Package.Length>0);openButton.Enabled=!working&&(a.Path.Length>0||a.Executable.Length>0);residualButton.Enabled=!working&&!a.Protected;appMenu.Items[0].Enabled=!working&&!a.Protected&&a.Path.Length>0;appMenu.Items[1].Enabled=!working&&a.Registration==RegistryStatus.Stale;
            measureButton.Enabled=!working&&!a.Protected&&!a.NeedsRegistrationReview;Image img;detailIcon.Image=appIcons.TryGetValue(a.Id,out img)?img:null;RequestIcon(a);
        }
    }
}
