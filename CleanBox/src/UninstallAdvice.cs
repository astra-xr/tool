using System;
using System.Collections.Generic;

namespace CleanBox {
    internal sealed class UninstallAdvice {
        internal readonly string Category, Recommendation, Purpose, Impact, Evidence;

        UninstallAdvice(string category, string recommendation, string purpose, string impact, string evidence) {
            Category = category;
            Recommendation = recommendation;
            Purpose = purpose;
            Impact = impact;
            Evidence = evidence;
        }

        internal string DetailText {
            get { return Recommendation + "\r\n\r\n软件用途\r\n" + Purpose + "\r\n\r\n卸载影响\r\n" + Impact + "\r\n\r\n判断依据\r\n" + Evidence; }
        }

        internal static UninstallAdvice For(AppEntry app) {
            if (app.Registration == RegistryStatus.Stale)
                return new UninstallAdvice("失效登记", "无需卸载", "这是已失效的 Windows 卸载列表项。",
                    "清除这条记录只会移除卸载列表中的登记。清理前会再次检查安装迹象并备份记录。", "登记中可核实的安装路径已不存在。");
            if (app.Registration == RegistryStatus.Incomplete)
                return new UninstallAdvice("待核实登记", "先核实安装状态", ReportedPurpose(app, "根据现有信息无法确定软件用途。"),
                    "先在 Windows 设置中检查软件是否仍在使用，再决定是否处理。", "这条登记缺少可核实的安装信息。");

            if (app.IsPackageFramework)
                return new UninstallAdvice("共享框架（受保护）", "建议保留", "这个 MSIX 框架可为其他应用提供运行组件。",
                    "移除后，依赖该框架的应用可能无法安装、更新或启动。", "Windows 软件包标记为框架。");
            if (app.Protected) {
                bool package = app.Package.Length > 0;
                return new UninstallAdvice(package ? "系统保护软件包" : "系统标记组件", "建议保留",
                    ReportedPurpose(app, "该项目可能为 Windows 或其他软件提供功能。"),
                    "移除可能影响系统功能或其他软件。具体依赖需要核实。", package ? "Windows 标记该软件包不可移除。"
                    : "卸载登记带有系统组件标记。登记本身无法确定它是否属于 Windows 核心功能。");
            }
            if (CatalogClassification.Kind(app) == 2) return DescribeRuntime(app);
            if (app.RegistryPath.Length == 0 && app.Package.Length == 0)
                return new UninstallAdvice("磁盘或快捷方式候选", "先核实文件归属",
                    ReportedPurpose(app, "发现了程序文件或快捷方式。"), "文件可能属于更大的应用。处理前先核对安装位置和其中的个人数据。",
                    "未找到对应的 Windows 卸载登记。");

            Profile profile;
            if (app.Package.Length == 0 && profiles.TryGetValue(app.Name.Trim(), out profile)
                && Array.Exists(profile.Publishers, publisher => publisher.Equals(app.Publisher.Trim(), StringComparison.OrdinalIgnoreCase))) {
                return new UninstallAdvice("普通应用（非系统组件）", app.Command.Length > 0 ? "不用时可考虑卸载" : "先确认卸载入口",
                    profile.Purpose, profile.Impact, "软件名称和发布者与内置资料匹配。");
            }

            if (app.Package.Length > 0)
                return DescribeStoreApp(app);
            return new UninstallAdvice("已登记应用", "先核实用途", ReportedPurpose(app, "登记未提供可确认的用途。"),
                "卸载会移除这个应用的功能。设置、个人数据以及依赖它的程序可能受到影响。",
                "已找到 Windows 卸载登记。用途和依赖仍需核实。");
        }

        static UninstallAdvice DescribeStoreApp(AppEntry app) {
            string purpose;
            const string impact = "卸载后，当前用户将无法打开该应用，部分本地设置或数据可能随应用删除。";
            if (app.Package.EndsWith("_8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase)
                && app.Package.StartsWith(app.Name + "_", StringComparison.OrdinalIgnoreCase)
                && storePurposes.TryGetValue(app.Name, out purpose))
                return new UninstallAdvice("商店应用", "不用时可考虑卸载", purpose, impact, "Windows 软件包标识与内置资料匹配。");
            return new UninstallAdvice("商店应用", "先核实用途", ReportedPurpose(app, "当前用户安装的商店或 MSIX 应用。"),
                impact, "根据 Windows 软件包信息判断。具体用途和依赖需核实。");
        }

        static UninstallAdvice DescribeRuntime(AppEntry app) {
            string name = app.Name;
            const StringComparison ignoreCase = StringComparison.OrdinalIgnoreCase;
            string purpose = "为其他程序提供共享的运行或开发组件。";
            string impact = "移除后，依赖此组件的程序可能无法启动，或部分功能无法使用。";
            if (name.StartsWith("Microsoft Visual C++", ignoreCase))
                purpose = "为使用 Microsoft Visual C++ 构建的软件提供运行库。不同版本和架构可能被不同程序使用。";
            else if (name.StartsWith("Microsoft Edge WebView2", ignoreCase))
                purpose = "为其他应用提供内嵌网页、登录或帮助页面使用的浏览器组件。";
            else if (name.IndexOf(" SDK", ignoreCase) >= 0 || name.IndexOf(" Targeting Pack", ignoreCase) >= 0) {
                purpose = "为构建或调试 .NET 项目提供开发工具。";
                impact = "移除后，使用该版本的项目可能无法构建或调试。";
            }
            return new UninstallAdvice("共享运行库或开发组件", "建议保留", purpose, impact, "安装名称与已知组件系列匹配。");
        }

        static string ReportedPurpose(AppEntry app, string fallback) {
            return app.ReportedDescription.Length > 0 ? "登记或文件说明\r\n" + app.ReportedDescription : fallback;
        }

        sealed class Profile {
            internal readonly string Purpose, Impact;
            internal readonly string[] Publishers;
            internal Profile(string purpose, string impact, params string[] publishers) {
                Purpose = purpose; Impact = impact; Publishers = publishers;
            }
        }

        static readonly Dictionary<string,Profile> profiles = new Dictionary<string,Profile>(StringComparer.OrdinalIgnoreCase) {
            { "Google Chrome", new Profile("网页浏览器。", "卸载后无法用 Chrome 打开网页。先确认书签、密码和个人资料已同步或备份，并检查默认浏览器设置。", "Google LLC", "Google Inc.") },
            { "Microsoft Visual Studio Code", new Profile("代码编辑器。", "卸载后将失去 VS Code 的编辑、扩展和调试入口。项目文件通常仍在原位置，建议先检查设置同步。", "Microsoft Corporation") },
            { "Microsoft Visual Studio Code (User)", new Profile("当前用户安装的代码编辑器。", "卸载后将失去 VS Code 的编辑、扩展和调试入口。项目文件通常仍在原位置，建议先检查设置同步。", "Microsoft Corporation") },
            { "Steam", new Profile("游戏下载、更新和启动客户端。", "卸载后可能影响游戏启动和更新。先检查游戏库所在位置，并确认常用游戏存档已备份或同步。", "Valve Corporation", "Valve Corp.") },
            { "WeGame", new Profile("腾讯游戏平台，用于游戏下载、更新和启动。", "卸载后将失去 WeGame 的游戏启动和更新入口。先查看卸载器是否会移除已下载游戏或设置。", "Tencent", "WeGame", "Tencent Technology (Shenzhen) Company Limited", "腾讯") }
        };

        static readonly Dictionary<string,string> storePurposes = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
            { "Microsoft.WindowsCalculator", "Windows 计算器，用于计算和单位换算。" },
            { "Microsoft.WindowsNotepad", "Windows 记事本，用于编辑纯文本。卸载前请保存打开的草稿。" },
            { "Microsoft.Paint", "Windows 画图，用于简单绘图和图片编辑。" },
            { "Microsoft.Windows.Photos", "Windows 照片，用于浏览和编辑图像。卸载后可用其他应用打开图像。" },
            { "Microsoft.WindowsTerminal", "打开 PowerShell、命令提示符等命令行环境的终端窗口。" }
        };
    }
}
