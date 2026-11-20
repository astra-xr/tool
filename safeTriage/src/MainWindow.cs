using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SafeTriage
{
    internal sealed partial class MainWindow
    {
        private readonly Window window;
        private bool busy;
        private CancellationTokenSource captureStop;
        private CaptureMarks captureMarks;
        private static readonly string[] Pages = { "Home", "Network", "Special", "Timeline" };
        private static readonly string[] CheckButtons = { "QuickButton", "NetworkButton", "NetworkRecordButton",
            "PerformanceButton", "AudioButton", "UsbButton", "StartupButton", "StorageButton", "TimelineButton" };

        static MainWindow()
        {
            var moveLight = new MouseEventHandler(MoveButtonLight);
            EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.MouseEnterEvent, moveLight);
            EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.MouseMoveEvent, moveLight, true);
        }

        public MainWindow()
        {
            Assembly assembly = typeof(MainWindow).Assembly;
            using (var layout = assembly.GetManifestResourceStream("SafeTriage.MainWindow.xaml"))
                window = (Window)XamlReader.Load(layout);
            using (var icon = assembly.GetManifestResourceStream("SafeTriage.Icon.ico"))
                window.Icon = BitmapFrame.Create(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            window.SourceInitialized += SetTitleBar;
            var hero = Find<Border>("QuickHero");
            var heroGlow = Find<Border>("QuickHeroGlow");
            MouseEventHandler moveHeroLight = (sender, args) => PositionPointerLight(heroGlow, args.GetPosition(heroGlow));
            hero.MouseEnter += moveHeroLight;
            hero.AddHandler(UIElement.MouseMoveEvent, moveHeroLight, true);

            foreach (string page in Pages)
            {
                string selectedPage = page;
                Find<ToggleButton>(page + "Nav").Click += (sender, args) => ShowPage(selectedPage);
            }
            Find<Button>("NetworkShortcut").Click += (sender, args) => ShowPage("Network");
            Find<Button>("QuickButton").Click += async (sender, args) => await RunQuick();
            Find<Button>("NetworkButton").Click += async (sender, args) => await RunNetwork(false);
            Find<Button>("NetworkRecordButton").Click += async (sender, args) => await RunNetwork(true);
            Find<Button>("PerformanceButton").Click += async (sender, args) => await RunCapture("SpecialResults", "卡顿现场", "30 秒",
                (token, progress, marks) => PerformanceCapture.RunAsync(30, progress, token, marks));
            Find<Button>("AudioButton").Click += async (sender, args) => await RunCheck("SpecialResults", "声音排查", "", DeviceDiagnostics.AudioAsync);
            Find<Button>("UsbButton").Click += async (sender, args) => await RunCheck("SpecialResults", "USB 排查", "", DeviceDiagnostics.UsbAsync);
            Find<Button>("StartupButton").Click += async (sender, args) => await RunCheck("SpecialResults", "启动排查", "", StartupDiagnostics.RunAsync);
            Find<Button>("StorageButton").Click += async (sender, args) => await RunCheck("SpecialResults", "存储排查", "", StorageDiagnostics.RunAsync);
            Find<Button>("TimelineButton").Click += async (sender, args) => await RunTimeline();
            Find<Button>("StopCapture").Click += (sender, args) =>
            {
                if (captureStop != null)
                    captureStop.Cancel();
                Find<Button>("StopCapture").IsEnabled = false;
                Find<Button>("MarkCapture").IsEnabled = false;
                Find<TextBlock>("OperationStatus").Text = "正在保留已完成的采样…";
            };
            Find<Button>("MarkCapture").Click += (sender, args) =>
            {
                double offset;
                if (captureMarks == null || captureStop.IsCancellationRequested || !captureMarks.TryAdd(out offset)) return;
                var status = Find<TextBlock>("CaptureMarkStatus");
                status.Text = "已标记 " + CaptureMarks.At(offset);
                status.Visibility = Visibility.Visible;
            };
            window.Closed += (sender, args) => { if (captureStop != null) captureStop.Cancel(); };
            var target = Find<TextBox>("NetworkTarget");
            Find<Label>("TargetLabel").Target = target;
            target.TextChanged += (sender, args) =>
            {
                Find<TextBlock>("NetworkInputError").Visibility = Visibility.Collapsed;
                Find<Button>("NetworkButton").Content = "开始检测";
            };
            target.KeyDown += async (sender, args) =>
            {
                if (args.Key == Key.Enter)
                {
                    args.Handled = true;
                    await RunNetwork(false);
                }
            };
            var incident = Find<TextBox>("TimelineAt");
            incident.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            Find<Label>("TimelineLabel").Target = incident;
            incident.TextChanged += (sender, args) => Find<TextBlock>("TimelineInputError").Visibility = Visibility.Collapsed;
            incident.KeyDown += async (sender, args) =>
            {
                if (args.Key == Key.Enter)
                {
                    args.Handled = true;
                    await RunTimeline();
                }
            };
        }

        public Window Window { get { return window; } }

        private static void MoveButtonLight(object sender, MouseEventArgs args)
        {
            var button = (ButtonBase)sender;
            if (!button.IsEnabled || button.Template == null)
                return;

            var glow = button.Template.FindName("PointerGlow", button) as Border;
            if (glow != null)
                PositionPointerLight(glow, args.GetPosition(glow));
        }

        private static void PositionPointerLight(Border glow, Point position)
        {
            var fill = glow.Background as RadialGradientBrush;
            var rim = glow.BorderBrush as RadialGradientBrush;
            if (fill == null || rim == null)
                return;

            // Keep the shared brushes separate from the light on each control.
            if (glow.ReadLocalValue(Border.BackgroundProperty) == DependencyProperty.UnsetValue)
            {
                glow.Background = fill = fill.Clone();
                glow.BorderBrush = rim = rim.Clone();
            }

            fill.Center = fill.GradientOrigin = position;
            rim.Center = rim.GradientOrigin = position;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int size);

        private static void SetTitleBar(object sender, EventArgs args)
        {
            if (SystemParameters.HighContrast)
                return;

            var target = (Window)sender;
            IntPtr handle = new WindowInteropHelper(target).Handle;
            const int useImmersiveDarkMode = 20;
            const int captionColor = 35;
            int darkMode = 0;
            Color color = ((SolidColorBrush)target.Background).Color;
            int background = color.R | (color.G << 8) | (color.B << 16); // DWM COLORREF is ordered BGR.
            DwmSetWindowAttribute(handle, useImmersiveDarkMode, ref darkMode, sizeof(int));
            DwmSetWindowAttribute(handle, captionColor, ref background, sizeof(int));
        }

        private T Find<T>(string name) where T : FrameworkElement
        {
            return (T)window.FindName(name);
        }

        private void ShowPage(string selected)
        {
            foreach (string page in Pages)
            {
                Find<ToggleButton>(page + "Nav").IsChecked = page == selected;
                Find<StackPanel>(page + "Page").Visibility = page == selected ? Visibility.Visible : Visibility.Collapsed;
            }
            Find<ScrollViewer>("ContentScroll").ScrollToTop();
        }

        private void SetBusy(bool running, bool capture, string name)
        {
            busy = running;
            foreach (string button in CheckButtons)
                Find<Button>(button).IsEnabled = !running;
            Find<TextBox>("NetworkTarget").IsReadOnly = running;
            Find<TextBox>("TimelineAt").IsReadOnly = running;
            Find<Border>("OperationBanner").Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            Find<Button>("StopCapture").Visibility = capture ? Visibility.Visible : Visibility.Collapsed;
            Find<Button>("StopCapture").IsEnabled = capture;
            Find<Button>("MarkCapture").Visibility = capture ? Visibility.Visible : Visibility.Collapsed;
            Find<Button>("MarkCapture").IsEnabled = capture;
            Find<TextBlock>("CaptureMarkStatus").Visibility = Visibility.Collapsed;
            Find<TextBlock>("OperationStatus").Text = running ? name + " · 正在读取…" : "";
        }

        private Task RunQuick()
        {
            return RunCheck("QuickResults", "快速排查", "", Diagnostics.QuickAsync, completed: () =>
            {
                Find<StackPanel>("HomePreview").Visibility = Visibility.Collapsed;
                Find<Viewbox>("QuickIllustration").Height = 120;
                Find<Border>("QuickHero").MinHeight = 174;
                Find<Button>("QuickButton").Content = "复查并对比";
            });
        }

        private Task RunNetwork(bool record)
        {
            if (busy)
                return Task.FromResult(0);

            Uri target;
            var input = Find<TextBox>("NetworkTarget");
            var error = Find<TextBlock>("NetworkInputError");
            if (!NetworkDiagnostics.TryTarget(input.Text, out target))
            {
                error.Text = "请输入一个域名或 HTTPS 地址，例如 www.microsoft.com。";
                error.Visibility = Visibility.Visible;
                input.Focus();
                return Task.FromResult(0);
            }

            error.Visibility = Visibility.Collapsed;
            if (record)
                return RunCapture("NetworkResults", "间歇断网记录", target.AbsoluteUri,
                    (token, progress, marks) => NetworkCapture.RunAsync(target, 60, progress, token, marks), target.Authority,
                    () => Find<StackPanel>("NetworkPreview").Visibility = Visibility.Collapsed);
            return RunCheck("NetworkResults", "网络排查", target.AbsoluteUri, () => NetworkDiagnostics.RunAsync(target), target.Authority,
                completed: () =>
                {
                    Find<StackPanel>("NetworkPreview").Visibility = Visibility.Collapsed;
                    Find<Button>("NetworkButton").Content = "复查并对比";
                });
        }

        private Task RunTimeline()
        {
            if (busy)
                return Task.FromResult(0);
            var input = Find<TextBox>("TimelineAt");
            DateTime local;
            if (!DateTime.TryParseExact(input.Text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out local) ||
                local.Year < 2000 || local > DateTime.Now.AddMinutes(1) || TimeZoneInfo.Local.IsInvalidTime(local))
            {
                var error = Find<TextBlock>("TimelineInputError");
                error.Text = "请输入有效的本机日期和时间，格式为 年-月-日 时:分，时间应在当前或过去。";
                error.Visibility = Visibility.Visible;
                input.Focus();
                return Task.FromResult(0);
            }
            var time = new DateTimeOffset(local);
            return RunCheck("TimelineResults", "故障时间线", time.ToString("o"), () => TimelineDiagnostics.RunAsync(time),
                time.ToString("MM-dd HH:mm"));
        }

        private Task RunCapture(string output, string name, string scope, Func<CancellationToken, IProgress<string>, CaptureMarks, Task<Report>> operation,
            string subtitle = null, Action completed = null)
        {
            return RunCheck(output, name, scope, () =>
            {
                var source = captureStop;
                var progress = new Progress<string>(message =>
                {
                    if (ReferenceEquals(captureStop, source) && !source.IsCancellationRequested)
                        Find<TextBlock>("OperationStatus").Text = name + " · " + message;
                });
                return operation(source.Token, progress, captureMarks);
            }, subtitle, capture: true, completed: completed);
        }

        private async Task RunCheck(string output, string name, string scope, Func<Task<Report>> operation,
            string subtitle = null, bool capture = false, Action completed = null)
        {
            if (busy)
                return;
            if (capture)
            {
                captureStop = new CancellationTokenSource();
                captureMarks = new CaptureMarks();
            }
            SetBusy(true, capture, name);
            StackPanel results = Find<StackPanel>(output);
            try
            {
                Report report = await operation();
                if (completed != null)
                    completed();
                ShowResults(results, name, scope, subtitle, report);
            }
            catch (Exception)
            {
                ShowResults(results, name, scope, subtitle, new Report("本轮检测中断，可以重新检测。", new Finding[0]));
            }
            finally
            {
                if (captureStop != null)
                {
                    captureStop.Dispose();
                    captureStop = null;
                    captureMarks = null;
                }
                SetBusy(false, false, name);
            }
        }

        private Border BuildFinding(Finding finding, bool? expanded = null)
        {
            string label, foreground, background;
            switch (finding.State)
            {
                case CheckState.Clear: label = "已检查"; foreground = "#24604A"; background = "#EAF4ED"; break;
                case CheckState.Info: label = "供参考"; foreground = "#355A78"; background = "#EDF3F8"; break;
                case CheckState.Warning: label = "建议检查"; foreground = "#785017"; background = "#FFF2DA"; break;
                case CheckState.Error: label = "发现异常"; foreground = "#BA152E"; background = "#FFE8EB"; break;
                default: label = "未完成"; foreground = "#61575B"; background = "#F1EDEF"; break;
            }

            var header = new DockPanel { LastChildFill = true };
            var status = new Border
            {
                Background = Brush(background), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0), Child = Text(label, 12, Brush(foreground), FontWeights.SemiBold)
            };
            DockPanel.SetDock(status, Dock.Right);
            header.Children.Add(status);
            var title = Text(finding.Name, 14, ThemeBrush("Ink"), FontWeights.SemiBold);
            title.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(title);

            var body = new StackPanel();
            var detail = Text(finding.Detail, 13, ThemeBrush("Muted"), FontWeights.Normal);
            detail.Margin = new Thickness(0, 9, 0, 0);
            body.Children.Add(detail);
            if (!string.IsNullOrEmpty(finding.Advice))
            {
                var advice = Text(finding.Advice, 13, ThemeBrush("Ink"), FontWeights.Normal);
                advice.Margin = new Thickness(0, 6, 0, 0);
                body.Children.Add(advice);
            }
            if (finding.Action != null)
            {
                var action = new Button
                {
                    Style = (Style)window.Resources["SecondaryButton"], Content = ActionLabel(finding.Action),
                    Padding = new Thickness(10, 6, 10, 6), HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 9, 0, 0), FontSize = 12
                };
                action.Click += (sender, args) => OpenAction(finding.Action);
                body.Children.Add(action);
            }

            var expander = new Expander
            {
                Header = header, Content = body, Style = (Style)window.Resources["FindingExpander"],
                IsExpanded = expanded ?? (finding.State == CheckState.Warning || finding.State == CheckState.Error || finding.State == CheckState.Unavailable)
            };
            var tile = Tile(expander);
            tile.Margin = new Thickness(0, 8, 0, 0);
            tile.Padding = new Thickness(9, 7, 9, 7);
            return tile;
        }

        private string ActionLabel(string action)
        {
            switch (action)
            {
                case "network": return "继续排查网络";
                case "networkSettings": return "打开网络设置";
                case "proxySettings": return "打开代理设置";
                case "storage": return "打开存储设置";
                case "systemLog": return "打开事件查看器";
                case "taskmgr": return "打开任务管理器";
                case "reliability": return "查看可靠性历史";
                case "updates": return "打开更新历史记录";
                case "sound": return "打开声音设置";
                case "services": return "打开服务管理器";
                case "devices": return "打开设备管理器";
                case "startup": return "打开启动应用";
                case "resmon": return "打开资源监视器";
                default: return "打开 Windows 设置";
            }
        }

        private void OpenAction(string action)
        {
            try
            {
                switch (action)
                {
                    case "network": ShowPage("Network"); break;
                    case "networkSettings": Open("ms-settings:network-status"); break;
                    case "proxySettings": Open("ms-settings:network-proxy"); break;
                    case "storage": Open("ms-settings:storagesense"); break;
                    case "systemLog": Open(System.IO.Path.Combine(Environment.SystemDirectory, "eventvwr.msc")); break;
                    case "taskmgr": Open(System.IO.Path.Combine(Environment.SystemDirectory, "Taskmgr.exe")); break;
                    case "reliability": Open(System.IO.Path.Combine(Environment.SystemDirectory, "perfmon.exe"), "/rel"); break;
                    case "updates": Open("ms-settings:windowsupdate-history"); break;
                    case "sound": Open("ms-settings:sound"); break;
                    case "services": Open(System.IO.Path.Combine(Environment.SystemDirectory, "services.msc")); break;
                    case "devices": Open(System.IO.Path.Combine(Environment.SystemDirectory, "devmgmt.msc")); break;
                    case "startup": Open("ms-settings:startupapps"); break;
                    case "resmon": Open(System.IO.Path.Combine(Environment.SystemDirectory, "resmon.exe")); break;
                }
            }
            catch (Exception)
            {
                MessageBox.Show(window, "Windows 未能打开该页面，可以在开始菜单里搜索对应功能。", "安诊", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private static void Open(string target, string arguments = "")
        {
            using (Process process = Process.Start(new ProcessStartInfo(target, arguments) { UseShellExecute = true })) { }
        }

        private Border Tile(UIElement child)
        {
            return new Border { Style = (Style)window.Resources["Tile"], Child = child };
        }

        private Brush ThemeBrush(string name)
        {
            return (Brush)window.Resources[name];
        }

        private static TextBlock Text(string value, double size, Brush foreground, FontWeight weight)
        {
            return new TextBlock { Text = value, FontSize = size, Foreground = foreground, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
        }

        private static Brush Brush(string value)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            return brush;
        }
    }
}
