// 触摸设备托盘开关：常驻通知区域，左键弹出面板，可逐个启用/禁用触摸屏、手写笔、触摸板，支持开机自启动。
// 托盘程序以普通权限运行；开关设备由安装时注册的“后台助手”计划任务以最高权限代为执行，只在安装时需要一次管理员授权。
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TouchToggle
{
    class TouchDevice
    {
        public string InstanceId;
        public string Kind;
        public string Title;
        public string Detail;
        public bool Enabled;
    }

    static class Devices
    {
        // HID 数字化仪 (Usage Page 0x0D) 的用途编号 → 类型
        static readonly string[][] Kinds =
        {
            new[] { "HID_DEVICE_UP:000D_U:0004", "触摸屏" },
            new[] { "HID_DEVICE_UP:000D_U:0002", "手写笔" },
            new[] { "HID_DEVICE_UP:000D_U:0005", "触摸板" },
        };

        public static readonly string[] KindOrder = { "触摸屏", "手写笔", "触摸板" };

        public static List<TouchDevice> Load()
        {
            var screens = TouchToScreen();
            var list = new List<TouchDevice>();
            using (var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, HardwareID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass='HIDClass'"))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    var ids = mo["HardwareID"] as string[];
                    if (ids == null) continue;
                    string kind = null;
                    foreach (var k in Kinds)
                        if (Array.IndexOf(ids, k[0]) >= 0) { kind = k[1]; break; }
                    if (kind == null) continue;

                    string id = (string)mo["DeviceID"];
                    string name = (string)mo["Name"] ?? "";
                    object err = mo["ConfigManagerErrorCode"];
                    var d = new TouchDevice
                    {
                        InstanceId = id,
                        Kind = kind,
                        Enabled = err == null || Convert.ToUInt32(err) != 22,   // 22 = 设备已禁用
                    };
                    Describe(d, name, screens);
                    list.Add(d);
                }
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Title + a.InstanceId, b.Title + b.InstanceId));
            return list;
        }

        static void Describe(TouchDevice d, string name, Dictionary<string, string[]> screens)
        {
            // HID\ELAN9009&COL01\5&... → ELAN9009
            string[] parts = d.InstanceId.Split('\\');
            string hw = parts.Length > 1 ? parts[1].Split('&')[0] : d.InstanceId;
            bool generic = name.Length == 0 || name.StartsWith("符合") || name.StartsWith("HID-compliant");

            string[] scr;
            if (screens.TryGetValue(d.InstanceId.ToUpperInvariant(), out scr))
            {
                d.Title = scr[0] + d.Kind;
                d.Detail = hw + " · " + scr[1];
            }
            else if (hw.IndexOf("VHF", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                d.Title = "系统虚拟" + d.Kind;
                d.Detail = "由驱动或软件创建 (VHF)";
            }
            else
            {
                d.Title = generic ? hw + " " + d.Kind : name;
                d.Detail = generic ? "未关联到屏幕" : hw;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);

        // 触摸设备实例 ID → { "主屏幕"/"副屏幕", "1920×1080" }，依据“平板电脑设置”里的屏幕校准映射
        static Dictionary<string, string[]> TouchToScreen()
        {
            var monitors = new Dictionary<string, string[]>();
            foreach (Screen sc in Screen.AllScreens)
            {
                var dd = new DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf(dd);
                for (uint i = 0; EnumDisplayDevices(sc.DeviceName, i, ref dd, 1); i++)   // 1 = EDD_GET_DEVICE_INTERFACE_NAME
                {
                    if (!string.IsNullOrEmpty(dd.DeviceID))
                        monitors[dd.DeviceID.ToUpperInvariant()] = new[]
                        {
                            sc.Primary ? "主屏幕" : "副屏幕",
                            sc.Bounds.Width + "×" + sc.Bounds.Height,
                        };
                    dd.cb = Marshal.SizeOf(dd);
                }
            }

            var result = new Dictionary<string, string[]>();
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Wisp\Pen\Digimon"))
            {
                if (key == null) return result;
                foreach (string valueName in key.GetValueNames())
                {
                    // 20-\\?\HID#ELAN9008&Col01#5&2bed5342&0&0000#{guid} → HID\ELAN9008&COL01\5&2BED5342&0&0000
                    int start = valueName.IndexOf(@"\\?\", StringComparison.Ordinal);
                    int end = valueName.LastIndexOf("#{", StringComparison.Ordinal);
                    if (start < 0 || end <= start) continue;
                    string inst = valueName.Substring(start + 4, end - start - 4).Replace('#', '\\').ToUpperInvariant();
                    string display = (key.GetValue(valueName) as string ?? "").ToUpperInvariant();
                    string[] mon;
                    if (monitors.TryGetValue(display, out mon)) result[inst] = mon;
                }
            }
            return result;
        }

        // 需要管理员权限，只在后台助手中调用。返回 null 表示成功，否则为错误信息
        public static string SetEnabled(TouchDevice d, bool enable)
        {
            if (d.InstanceId.IndexOf('"') >= 0) return d.Title + "：设备 ID 无效";
            string output;
            int code = Shell.Run(Shell.Sys("pnputil.exe"),
                (enable ? "/enable-device " : "/disable-device ") + "\"" + d.InstanceId + "\"", out output);
            if (code == 0) return null;
            if (code == 3010) return d.Title + "：需要重启后生效";
            return d.Title + "：操作失败 (" + code + ")";
        }
    }

    static class Shell
    {
        // 系统程序一律用 System32 下的完整路径，避免从程序所在目录或 PATH 中加载同名程序
        public static string Sys(string exe) { return Path.Combine(Environment.SystemDirectory, exe); }

        public static int Run(string exe, string args, out string output)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                // 两个管道都异步读取，超时才能真正生效
                var buf = new StringBuilder();
                DataReceivedEventHandler collect = (o, e) => { if (e.Data != null) lock (buf) buf.AppendLine(e.Data); };
                p.OutputDataReceived += collect;
                p.ErrorDataReceived += collect;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(30000))
                {
                    try { p.Kill(); } catch (Exception) { }
                    output = "";
                    return -1;
                }
                p.WaitForExit();
                lock (buf) output = buf.ToString();
                return p.ExitCode;
            }
        }
    }

    // 后台助手：安装时注册一个以 SYSTEM 身份运行、只允许当前用户按需启动的计划任务。
    // 托盘程序把请求写入 %ProgramData%\TouchToggle\queue\*.req（目录由安装程序创建，仅 SYSTEM、管理员和该用户可访问），
    // 再启动任务。助手对队列只读不写、不删，避免被符号链接利用；托盘程序轮询设备状态确认结果后自行删除请求。
    static class Helper
    {
        public const string TaskName = "TouchToggle-Helper";
        const string LegacyTaskName = "TouchToggle";   // 旧版本用于开机自启动的计划任务
        static readonly object requestLock = new object();

        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TouchToggle"); }
        }

        public static string InstalledExe { get { return Path.Combine(InstallDir, "TouchToggle.exe"); } }

        static string DataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TouchToggle"); }
        }

        static string QueueDir { get { return Path.Combine(DataDir, "queue"); } }

        public static bool SamePath(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }

        public static bool RunningInstalled { get { return SamePath(Application.ExecutablePath, InstalledExe); } }

        public static bool IsInstalled()
        {
            string o;
            return File.Exists(InstalledExe) && Directory.Exists(QueueDir)
                && Shell.Run(Shell.Sys("schtasks.exe"), "/query /tn " + TaskName, out o) == 0;
        }

        // ---- 托盘程序（普通权限）一侧 ----

        public static string Install()
        {
            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            return Elevate(Application.ExecutablePath, "--install \"" + user + "\"", "安装");
        }

        public static string Uninstall()
        {
            string exe = File.Exists(InstalledExe) ? InstalledExe : Application.ExecutablePath;
            return Elevate(exe, "--uninstall", "卸载");
        }

        static string Elevate(string exe, string args, string what)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas" };
            try
            {
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    return p.ExitCode == 0 ? null : what + "失败 (" + p.ExitCode + ")";
                }
            }
            catch (Win32Exception) { return "已取消" + what; }
        }

        // 同一时间只发出一个请求，助手按顺序处理，不会出现先后颠倒
        public static string Request(List<TouchDevice> targets, bool on)
        {
            lock (requestLock)
            {
                string req = Path.Combine(QueueDir, Guid.NewGuid().ToString("N") + ".req");
                var sb = new StringBuilder();
                foreach (var d in targets) sb.AppendLine((on ? "1 " : "0 ") + d.InstanceId);
                try { File.WriteAllText(req, sb.ToString(), Encoding.UTF8); }
                catch (Exception) { return "无法提交请求，请重新安装后台助手"; }

                try
                {
                    string o;
                    if (Shell.Run(Shell.Sys("schtasks.exe"), "/run /tn " + TaskName, out o) != 0)
                        return "无法启动后台助手，请重新安装";

                    // 轮询设备状态，直到全部生效或超时
                    var pending = new List<TouchDevice>(targets);
                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 15000)
                    {
                        Thread.Sleep(250);
                        var now = Devices.Load();
                        pending.RemoveAll(d =>
                        {
                            TouchDevice cur = now.Find(x => x.InstanceId == d.InstanceId);
                            return cur == null || cur.Enabled == on;
                        });
                        if (pending.Count == 0) return null;
                    }
                    return string.Join("、", pending.ConvertAll(d => d.Title).ToArray()) + "：未生效，可能需要重启";
                }
                finally
                {
                    try { File.Delete(req); } catch (Exception) { }
                }
            }
        }

        static bool IsLink(string path)
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }

        // ---- 管理员一侧 ----

        public static int DoInstall(string user)
        {
            try
            {
                var sid = (SecurityIdentifier)new NTAccount(user).Translate(typeof(SecurityIdentifier));

                Directory.CreateDirectory(InstallDir);
                foreach (string f in Directory.GetFiles(InstallDir, "*.old"))
                    try { File.Delete(f); } catch (Exception) { }
                if (!RunningInstalled)
                {
                    try { File.Copy(Application.ExecutablePath, InstalledExe, true); }
                    catch (IOException)
                    {
                        // 旧版本正在运行时无法覆盖，但可以改名
                        File.Move(InstalledExe, InstalledExe + "." + Guid.NewGuid().ToString("N") + ".old");
                        File.Copy(Application.ExecutablePath, InstalledExe, true);
                    }
                }

                CreateDataDir(sid);

                // 任务以 SYSTEM 运行（不继承用户环境变量）；只授予该用户读取和运行任务的权限
                string xml =
                    "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
                    "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
                    "  <RegistrationInfo><Description>TouchToggle 后台助手：按托盘程序的请求启用/禁用触摸设备</Description>" +
                    "<SecurityDescriptor>D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGX;;;" + sid.Value + ")</SecurityDescriptor></RegistrationInfo>\n" +
                    "  <Principals><Principal id=\"Author\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\n" +
                    "  <Settings><MultipleInstancesPolicy>Queue</MultipleInstancesPolicy><AllowStartOnDemand>true</AllowStartOnDemand>" +
                    "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
                    "<ExecutionTimeLimit>PT5M</ExecutionTimeLimit><Hidden>true</Hidden><Priority>5</Priority></Settings>\n" +
                    "  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(InstalledExe) + "</Command>" +
                    "<Arguments>--helper</Arguments></Exec></Actions>\n" +
                    "</Task>\n";
                // 写在只有管理员可写的安装目录里，避免注册前被篡改
                string tmp = Path.Combine(InstallDir, "task.xml");
                File.WriteAllText(tmp, xml, Encoding.Unicode);
                string o;
                int code;
                try { code = Shell.Run(Shell.Sys("schtasks.exe"), "/create /tn " + TaskName + " /xml \"" + tmp + "\" /f", out o); }
                finally { File.Delete(tmp); }
                if (code != 0) return 2;

                Shell.Run(Shell.Sys("schtasks.exe"), "/delete /tn " + LegacyTaskName + " /f", out o);
                return 0;
            }
            catch (Exception) { return 1; }
        }

        // %ProgramData%\TouchToggle：SYSTEM 和管理员完全控制，该用户只读；queue 子目录该用户可修改
        static void CreateDataDir(SecurityIdentifier user)
        {
            // ProgramData 下普通用户也能建目录，若已存在（可能被抢先创建或是联接），删掉重建，确保所有者是管理员
            if (Directory.Exists(DataDir))
            {
                if (IsLink(DataDir)) Directory.Delete(DataDir);
                else Directory.Delete(DataDir, true);
            }

            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            var top = new DirectorySecurity();
            top.SetAccessRuleProtection(true, false);
            top.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            top.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            top.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            top.SetOwner(admins);
            Directory.CreateDirectory(DataDir, top);

            var queue = new DirectorySecurity();
            queue.SetAccessRuleProtection(true, false);
            queue.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            queue.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            queue.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Modify, inherit, PropagationFlags.None, AccessControlType.Allow));
            queue.SetOwner(admins);
            Directory.CreateDirectory(QueueDir, queue);
        }

        public static int DoUninstall()
        {
            string o;
            Shell.Run(Shell.Sys("schtasks.exe"), "/delete /tn " + TaskName + " /f", out o);
            Shell.Run(Shell.Sys("schtasks.exe"), "/delete /tn " + LegacyTaskName + " /f", out o);
            try { if (Directory.Exists(DataDir)) Directory.Delete(DataDir, true); } catch (Exception) { }
            // 等托盘程序退出后再删除安装目录
            var psi = new ProcessStartInfo(Shell.Sys("cmd.exe"),
                "/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"" + InstallDir + "\"")
            { UseShellExecute = false, CreateNoWindow = true };
            Process.Start(psi);
            return 0;
        }

        // 由计划任务以 SYSTEM 身份启动：执行队列中的请求。对队列只读；只接受当前枚举到的触摸类设备；
        // 忽略两分钟前的旧请求（托盘程序异常退出时可能残留）。
        public static int Serve()
        {
            string dir = QueueDir;
            if (!Directory.Exists(dir) || IsLink(dir) || IsLink(DataDir)) return 0;
            List<TouchDevice> devices = Devices.Load();
            var files = new List<FileInfo>(new DirectoryInfo(dir).GetFiles("*.req"));
            files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            foreach (var f in files)
            {
                try
                {
                    if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromMinutes(2)) continue;
                    foreach (string line in File.ReadAllLines(f.FullName, Encoding.UTF8))
                    {
                        if (line.Length < 3 || (line[0] != '0' && line[0] != '1') || line[1] != ' ') continue;
                        string id = line.Substring(2).Trim();
                        TouchDevice d = devices.Find(x => string.Equals(x.InstanceId, id, StringComparison.OrdinalIgnoreCase));
                        if (d != null && d.Enabled != (line[0] == '1')) Devices.SetEnabled(d, line[0] == '1');
                    }
                }
                catch (Exception) { }
            }
            return 0;
        }
    }

    // 开机自启动写在当前用户的 Run 键，无需管理员
    static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "TouchToggle";

        public static bool IsEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(ValueName) != null;
        }

        public static string Set(bool on, string exe = null)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (!on) { k.DeleteValue(ValueName, false); return null; }
                    if (exe == null) exe = File.Exists(Helper.InstalledExe) ? Helper.InstalledExe : Application.ExecutablePath;
                    k.SetValue(ValueName, "\"" + exe + "\"");
                    return null;
                }
            }
            catch (Exception) { return on ? "开启开机自启动失败" : "关闭开机自启动失败"; }
        }
    }

    // 跟随系统深色/浅色模式与强调色
    class Theme
    {
        public bool Dark;
        public Color Bg, Card, Hover, Text, Sub, Divider, Border, IconOff, ToggleOff, KnobOn, Footer, Accent, AccentSoft, ErrBg, ErrText;

        public static Theme Current(bool? forceDark)
        {
            bool dark = true;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    if (v is int) dark = (int)v == 0;
                }
            }
            catch (Exception) { }
            if (forceDark.HasValue) dark = forceDark.Value;

            var t = new Theme { Dark = dark };
            if (dark)
            {
                t.Bg = Rgb(32, 32, 32); t.Card = Rgb(43, 43, 43); t.Hover = Rgb(52, 52, 52);
                t.Text = Rgb(255, 255, 255); t.Sub = Rgb(160, 160, 160); t.Divider = Rgb(30, 30, 30);
                t.Border = Rgb(64, 64, 64); t.IconOff = Rgb(58, 58, 58); t.ToggleOff = Rgb(205, 205, 205);
                t.KnobOn = Rgb(0, 0, 0); t.Footer = Rgb(28, 28, 28);
                t.ErrBg = Rgb(68, 39, 38); t.ErrText = Rgb(255, 153, 164);
            }
            else
            {
                t.Bg = Rgb(243, 243, 243); t.Card = Rgb(255, 255, 255); t.Hover = Rgb(245, 245, 245);
                t.Text = Rgb(27, 27, 27); t.Sub = Rgb(96, 96, 96); t.Divider = Rgb(234, 234, 234);
                t.Border = Rgb(214, 214, 214); t.IconOff = Rgb(236, 236, 236); t.ToggleOff = Rgb(135, 135, 135);
                t.KnobOn = Rgb(255, 255, 255); t.Footer = Rgb(236, 236, 236);
                t.ErrBg = Rgb(253, 231, 233); t.ErrText = Rgb(196, 43, 28);
            }
            t.Accent = ReadAccent(dark);
            t.AccentSoft = Blend(t.Card, t.Accent, dark ? 0.22f : 0.14f);
            return t;
        }

        static Color ReadAccent(bool dark)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    var p = k == null ? null : k.GetValue("AccentPalette") as byte[];
                    if (p != null && p.Length >= 32)
                    {
                        int i = dark ? 1 : 4;   // 深色用 Light2，浅色用 Dark1，和系统开关一致
                        return Rgb(p[i * 4], p[i * 4 + 1], p[i * 4 + 2]);
                    }
                }
            }
            catch (Exception) { }
            return dark ? Rgb(96, 205, 255) : Rgb(0, 95, 184);
        }

        public static Color Rgb(int r, int g, int b) { return Color.FromArgb(r, g, b); }

        public static Color Blend(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
    }

    class Item
    {
        public RectangleF R;
        public bool Hover;
        public Action Click;
    }

    class RowItem : Item
    {
        public string Key, Glyph, Title, Detail;
        public bool On, Busy, Last, NoToggle;
        public float Anim;
        public bool Seen;
        public TouchDevice Device;
    }

    class LinkItem : Item
    {
        public string Glyph, Text;
        public bool Accent, Filled;
    }

    // 仿 Windows 快速设置的弹出面板，全部自绘
    class Flyout : Form
    {
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point pt, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);

        const string GlyphTouch = "\uE7C9", GlyphPen = "\uEDC6", GlyphTouchpad = "\uEFA5", GlyphShield = "\uEA18",
                     GlyphRefresh = "\uE72C", GlyphPower = "\uE7E8", GlyphStartup = "\uE7B5";

        public event Action<List<TouchDevice>> DevicesChanged;
        public event Action Installed;
        public event Action Uninstalled;

        Theme T;
        float s = 1;
        Font fTitle, fSub, fRow, fSmall, fSection, fGlyph, fGlyphSmall;

        List<TouchDevice> devices = new List<TouchDevice>();
        bool autoStart, installed = true, installing;
        string error;
        readonly Dictionary<string, RowItem> rows = new Dictionary<string, RowItem>();
        readonly List<RowItem> drawRows = new List<RowItem>();
        readonly List<Item> clickables = new List<Item>();
        readonly List<KeyValuePair<RectangleF, string>> sections = new List<KeyValuePair<RectangleF, string>>();
        readonly List<RectangleF> cards = new List<RectangleF>();
        readonly LinkItem refreshBtn = new LinkItem { Glyph = GlyphRefresh };
        readonly LinkItem exitBtn = new LinkItem { Glyph = GlyphPower, Text = "退出" };
        readonly LinkItem installBtn = new LinkItem { Text = "立即安装", Filled = true };
        readonly LinkItem uninstallBtn = new LinkItem { Text = "卸载" };
        RectangleF errorRect, installRect, footerRect;
        Item hovered;

        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 15 };
        Point anchor;
        float slide;        // 弹出时的上滑距离（逻辑像素）
        DateTime hiddenAt = DateTime.MinValue;

        public Flyout()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Text = "触摸设备";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            refreshBtn.Click = () => { error = null; Reload(); };
            exitBtn.Click = () => Application.Exit();
            installBtn.Click = Install;
            uninstallBtn.Click = Uninstall;
            anim.Tick += (o, e) => Animate();
            ApplyTheme(null, 1);
            CreateHandle();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;   // CS_DROPSHADOW
                cp.ExStyle |= 0x80;         // WS_EX_TOOLWINDOW：不出现在 Alt+Tab
                return cp;
            }
        }

        public void ApplyTheme(bool? forceDark, float scale)
        {
            T = Theme.Current(forceDark);
            s = scale;
            string ui = "Microsoft YaHei UI";
            Font[] old = { fTitle, fSub, fRow, fSmall, fSection, fGlyph, fGlyphSmall };
            fTitle = new Font(ui, 15 * s, FontStyle.Bold, GraphicsUnit.Pixel);
            fSub = new Font(ui, 12 * s, GraphicsUnit.Pixel);
            fRow = new Font(ui, 14 * s, GraphicsUnit.Pixel);
            fSmall = new Font(ui, 12 * s, GraphicsUnit.Pixel);
            fSection = new Font(ui, 12 * s, FontStyle.Bold, GraphicsUnit.Pixel);
            fGlyph = new Font("Segoe MDL2 Assets", 16 * s, GraphicsUnit.Pixel);
            fGlyphSmall = new Font("Segoe MDL2 Assets", 14 * s, GraphicsUnit.Pixel);
            foreach (var f in old) if (f != null) f.Dispose();
            BackColor = T.Bg;
            LayoutItems();
        }

        float S(float v) { return v * s; }

        // ---------- 数据 ----------

        public void Reload() { Reload(true); }

        // checkInstall 为 false 时跳过安装状态查询（需要启动 schtasks），用于后台定时刷新
        public void Reload(bool checkInstall)
        {
            List<TouchDevice> list = null;
            bool auto = autoStart, inst = installed;
            Background(() =>
            {
                list = Devices.Load();
                auto = AutoStart.IsEnabled();
                if (checkInstall) inst = Helper.IsInstalled();
            }, () =>
            {
                if (list == null) error = "读取设备列表失败";
                else SetDevices(list);
                autoStart = auto;
                installed = inst;
                LayoutItems();
            });
        }

        // 在后台线程执行 work，再回到界面线程执行 done；异常不会让程序崩溃，程序退出后不再回调
        void Background(Action work, Action done)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { work(); }
                catch (Exception ex) { Debug.WriteLine(ex); }
                try { if (IsHandleCreated && !IsDisposed) BeginInvoke(done); }
                catch (InvalidOperationException) { }
            });
        }

        public void LoadNow()
        {
            SetDevices(Devices.Load());
            autoStart = AutoStart.IsEnabled();
            installed = Helper.IsInstalled();
            LayoutItems();
        }

        void SetDevices(List<TouchDevice> list)
        {
            devices = list;
            if (DevicesChanged != null) DevicesChanged(list);
        }

        void Run(List<RowItem> targets, bool on)
        {
            if (!installed)
            {
                error = "请先点击上方的“立即安装”";
                LayoutItems();
                return;
            }
            var todo = targets.FindAll(r => !r.Busy && r.On != on);
            if (todo.Count == 0) return;
            foreach (var r in todo) { r.On = on; r.Busy = true; }
            anim.Start();
            Invalidate();

            var list = todo.ConvertAll(r => r.Device);
            string err = "操作失败";
            List<TouchDevice> fresh = null;
            Background(() =>
            {
                err = Helper.Request(list, on);
                fresh = Devices.Load();
            }, () =>
            {
                foreach (var r in todo) r.Busy = false;
                error = err;
                if (fresh != null) SetDevices(fresh);
                LayoutItems();
                anim.Start();
            });
        }

        void ToggleAutoStart(RowItem r)
        {
            bool on = !r.On;
            error = AutoStart.Set(on);
            autoStart = AutoStart.IsEnabled();
            LayoutItems();
            anim.Start();
        }

        void Install()
        {
            if (installing) return;
            installing = true;
            installBtn.Text = "等待授权…";
            Invalidate();
            string err = "安装失败";
            Background(() => err = Helper.Install(), () =>
            {
                installing = false;
                installBtn.Text = "立即安装";
                error = err;
                // 成功时 Installed 可能会让程序换到安装目录重新启动，之后不再刷新
                if (err == null && Installed != null) Installed();
                else Reload();
            });
        }

        void Uninstall()
        {
            if (installing) return;
            installing = true;
            string err = "卸载失败";
            Background(() => err = Helper.Uninstall(), () =>
            {
                installing = false;
                error = err;
                if (err == null && Uninstalled != null) Uninstalled();
                if (!IsDisposed) Reload();
            });
        }

        // ---------- 布局 ----------

        RowItem GetRow(string key)
        {
            RowItem r;
            if (!rows.TryGetValue(key, out r))
            {
                r = new RowItem { Key = key };
                rows[key] = r;
            }
            return r;
        }

        // 新出现的行直接停在当前状态，不播放开关动画
        static void Settle(RowItem r)
        {
            if (!r.Seen) { r.Anim = r.On ? 1 : 0; r.Seen = true; }
        }

        void LayoutItems()
        {
            float W = S(360), pad = S(12), rowH = S(62);
            clickables.Clear(); drawRows.Clear(); sections.Clear(); cards.Clear();
            float y = S(18);

            refreshBtn.R = new RectangleF(W - S(16) - S(34), y + S(2), S(34), S(34));
            clickables.Add(refreshBtn);
            y += S(52);

            if (error != null)
            {
                errorRect = new RectangleF(pad, y, W - 2 * pad, S(40));
                y += S(50);
            }
            else errorRect = RectangleF.Empty;

            if (!installed)
            {
                installRect = new RectangleF(pad, y, W - 2 * pad, S(120));
                installBtn.R = new RectangleF(installRect.Right - S(16) - S(104), installRect.Bottom - S(16) - S(32), S(104), S(32));
                clickables.Add(installBtn);
                y += installRect.Height + S(16);
            }
            else installRect = RectangleF.Empty;

            var live = new HashSet<string>();
            foreach (string kind in Devices.KindOrder)
            {
                var group = devices.FindAll(d => d.Kind == kind);
                if (group.Count == 0) continue;

                var sec = new RectangleF(S(20), y, W - S(40), S(22));
                sections.Add(new KeyValuePair<RectangleF, string>(sec, kind));
                var groupRows = new List<RowItem>();
                y += S(26);

                var card = new RectangleF(pad, y, W - 2 * pad, group.Count * rowH);
                cards.Add(card);
                for (int i = 0; i < group.Count; i++)
                {
                    var d = group[i];
                    var r = GetRow(d.InstanceId);
                    live.Add(r.Key);
                    if (!r.Busy) r.On = d.Enabled;
                    Settle(r);
                    r.Device = d;
                    r.Title = d.Title;
                    r.Detail = d.Detail;
                    r.Glyph = kind == "触摸屏" ? GlyphTouch : kind == "手写笔" ? GlyphPen : GlyphTouchpad;
                    r.Last = i == group.Count - 1;
                    r.R = new RectangleF(card.X, y + i * rowH, card.Width, rowH);
                    var row = r;
                    r.Click = () => Run(new List<RowItem> { row }, !row.On);
                    clickables.Add(r);
                    drawRows.Add(r);
                    groupRows.Add(r);
                }

                if (group.Count > 1)
                {
                    bool anyOn = group.Exists(d => d.Enabled);
                    var all = new LinkItem { Text = anyOn ? "全部关闭" : "全部开启", Accent = true };
                    all.R = new RectangleF(sec.Right - S(76), sec.Y - S(4), S(80), S(28));
                    all.Click = () => Run(groupRows, !anyOn);
                    clickables.Add(all);
                }
                y += card.Height + S(16);
            }

            if (devices.Count == 0)
            {
                sections.Add(new KeyValuePair<RectangleF, string>(new RectangleF(S(20), y, W - S(40), S(22)), "设备"));
                y += S(26);
                cards.Add(new RectangleF(pad, y, W - 2 * pad, rowH));
                sections.Add(new KeyValuePair<RectangleF, string>(new RectangleF(pad + S(16), y, W - 2 * pad, rowH), "\0未检测到触摸设备"));
                y += rowH + S(16);
            }

            // 设置
            sections.Add(new KeyValuePair<RectangleF, string>(new RectangleF(S(20), y, W - S(40), S(22)), "设置"));
            y += S(26);
            var setCard = new RectangleF(pad, y, W - 2 * pad, (installed ? 2 : 1) * rowH);
            cards.Add(setCard);

            var auto = GetRow("::autostart");
            live.Add(auto.Key);
            auto.On = autoStart;
            Settle(auto);
            auto.Title = "开机自启动";
            auto.Detail = "登录 Windows 后自动在托盘运行";
            auto.Glyph = GlyphStartup;
            auto.Last = !installed;
            auto.R = new RectangleF(setCard.X, y, setCard.Width, rowH);
            auto.Click = () => ToggleAutoStart(auto);
            clickables.Add(auto);
            drawRows.Add(auto);

            if (installed)
            {
                var helper = GetRow("::helper");
                live.Add(helper.Key);
                helper.On = true;
                helper.NoToggle = true;
                Settle(helper);
                helper.Title = "后台助手";
                helper.Detail = "已安装 · 开关设备无需管理员";
                helper.Glyph = GlyphShield;
                helper.Last = true;
                helper.R = new RectangleF(setCard.X, y + rowH, setCard.Width, rowH);
                drawRows.Add(helper);
                uninstallBtn.R = new RectangleF(setCard.Right - S(12) - S(56), helper.R.Y + (rowH - S(30)) / 2, S(56), S(30));
                clickables.Add(uninstallBtn);
            }
            y += setCard.Height + S(18);

            footerRect = new RectangleF(0, y, W, S(56));
            exitBtn.R = new RectangleF(W - S(16) - S(84), y + S(12), S(84), S(32));
            clickables.Add(exitBtn);
            y += footerRect.Height;

            foreach (var k in new List<string>(rows.Keys))
                if (!live.Contains(k)) rows.Remove(k);
            if (hovered != null && !clickables.Contains(hovered)) hovered = null;

            var size = new Size((int)Math.Ceiling(W), (int)Math.Ceiling(y));
            if (Size != size)
            {
                Size = size;
                IntPtr rgn = CreateRoundRectRgn(0, 0, size.Width + 1, size.Height + 1, (int)S(16), (int)S(16));
                Region = Region.FromHrgn(rgn);
                DeleteObject(rgn);   // Region.FromHrgn 会复制一份
            }
            if (Visible) Place();
            Invalidate();
        }

        // ---------- 显示 / 隐藏 ----------

        public void ToggleAt(Point cursor)
        {
            if (Visible) { HideFlyout(); return; }
            // 点托盘图标会先让面板失焦隐藏，紧接着的这次点击不应再次打开
            if ((DateTime.Now - hiddenAt).TotalMilliseconds < 300) return;

            uint dx, dy;
            float scale = 1;
            try { if (GetDpiForMonitor(MonitorFromPoint(cursor, 2), 0, out dx, out dy) == 0) scale = dx / 96f; }
            catch (Exception) { }   // Windows 8.1 之前没有该函数
            error = null;
            ApplyTheme(null, scale);
            Reload();

            anchor = cursor;
            slide = 14;
            Opacity = 0;
            Place();
            Show();
            Activate();
            SetForegroundWindow(Handle);
            anim.Start();
        }

        void Place()
        {
            Rectangle wa = Screen.FromPoint(anchor).WorkingArea;
            int m = (int)S(12);
            bool right = anchor.X > wa.Left + wa.Width / 2;
            bool bottom = anchor.Y > wa.Top + wa.Height / 2;
            int x = right ? wa.Right - Width - m : wa.Left + m;
            int y = bottom ? wa.Bottom - Height - m : wa.Top + m;
            y += (int)(S(slide) * (bottom ? 1 : -1));
            Location = new Point(x, y);
        }

        void HideFlyout()
        {
            Hide();
            hiddenAt = DateTime.Now;
            if (hovered != null) hovered.Hover = false;
            hovered = null;
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            HideFlyout();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { HideFlyout(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void Animate()
        {
            bool busy = false;
            if (Visible && (Opacity < 1 || slide > 0))
            {
                Opacity = Math.Min(1, Opacity + 0.14);
                slide = Math.Max(0, slide * 0.7f - 0.3f);
                Place();
                busy = true;
            }
            foreach (var r in rows.Values)
            {
                float target = r.On ? 1 : 0;
                if (Math.Abs(r.Anim - target) > 0.01f)
                {
                    r.Anim += (target - r.Anim) * 0.3f;
                    busy = true;
                }
                else r.Anim = target;
                if (r.Busy) busy = true;
            }
            Invalidate();
            if (!busy) anim.Stop();
        }

        // ---------- 鼠标 ----------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Item hit = clickables.Find(c => c.R.Contains(e.Location));
            if (hit == hovered) return;
            if (hovered != null) hovered.Hover = false;
            hovered = hit;
            if (hit != null) hit.Hover = true;
            Cursor = hit != null ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hovered != null) { hovered.Hover = false; hovered = null; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            Item hit = clickables.Find(c => c.R.Contains(e.Location));
            if (hit != null && hit.Click != null) hit.Click();
        }

        // ---------- 绘制 ----------

        protected override void OnPaint(PaintEventArgs e) { Render(e.Graphics); }

        public Bitmap Snapshot()
        {
            var bmp = new Bitmap(Width, Height);
            using (var g = Graphics.FromImage(bmp)) Render(g);
            return bmp;
        }

        void Render(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(T.Bg);

            // 标题
            int on = 0, total = 0;
            foreach (var d in devices) if (d.Kind == "触摸屏") { total++; if (d.Enabled) on++; }
            Text_(g, "触摸设备", fTitle, T.Text, new RectangleF(S(20), S(16), S(260), S(24)));
            string status = total == 0 ? "未检测到触摸屏" : on == total ? "所有触摸屏已开启" : on == 0 ? "所有触摸屏已关闭" : on + " / " + total + " 块触摸屏已开启";
            Text_(g, status, fSub, T.Sub, new RectangleF(S(20), S(40), S(260), S(20)));
            DrawLink(g, refreshBtn);

            if (!errorRect.IsEmpty)
            {
                Fill(g, errorRect, S(6), T.ErrBg);
                Text_(g, error, fSmall, T.ErrText, new RectangleF(errorRect.X + S(14), errorRect.Y, errorRect.Width - S(28), errorRect.Height));
            }

            if (!installRect.IsEmpty)
            {
                Fill(g, installRect, S(8), T.AccentSoft);
                using (var p = Round(Inset(installRect, 0.5f), S(8)))
                using (var pen = new Pen(Color.FromArgb(90, T.Accent)))
                    g.DrawPath(pen, p);
                Glyph(g, GlyphShield, fGlyph, T.Accent, new RectangleF(installRect.X + S(14), installRect.Y + S(14), S(24), S(24)));
                Text_(g, "首次使用需安装后台助手", fRow, T.Text, new RectangleF(installRect.X + S(46), installRect.Y + S(14), installRect.Width - S(60), S(24)));
                TextRenderer.DrawText(g, "只需授权一次管理员权限，之后开关触摸设备不再弹出提示。", fSmall,
                    Rect(new RectangleF(installRect.X + S(46), installRect.Y + S(40), installRect.Width - S(60), S(36))), T.Sub,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }

            foreach (var sec in sections)
            {
                if (sec.Value.StartsWith("\0")) continue;
                Text_(g, sec.Value, fSection, T.Sub, sec.Key);
            }
            foreach (var card in cards)
            {
                Fill(g, card, S(8), T.Card);
                using (var p = Round(Inset(card, 0.5f), S(8)))
                using (var pen = new Pen(T.Dark ? Theme.Rgb(52, 52, 52) : Theme.Rgb(229, 229, 229)))
                    g.DrawPath(pen, p);
            }
            foreach (var sec in sections)
                if (sec.Value.StartsWith("\0")) Text_(g, sec.Value.Substring(1), fRow, T.Sub, sec.Key);

            foreach (var r in drawRows) DrawRow(g, r);
            foreach (var c in clickables)
            {
                var l = c as LinkItem;
                if (l != null && l != refreshBtn && l != exitBtn) DrawLink(g, l);
            }

            // 底栏
            using (var b = new SolidBrush(T.Footer)) g.FillRectangle(b, footerRect);
            using (var pen = new Pen(T.Dark ? Theme.Rgb(44, 44, 44) : Theme.Rgb(224, 224, 224)))
                g.DrawLine(pen, footerRect.X, footerRect.Y, footerRect.Right, footerRect.Y);
            Text_(g, "更改立即生效", fSmall, T.Sub, new RectangleF(S(20), footerRect.Y, S(200), footerRect.Height));
            DrawLink(g, exitBtn);

            // 窗口描边
            using (var p = Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), S(8)))
            using (var pen = new Pen(T.Border))
                g.DrawPath(pen, p);
        }

        void DrawRow(Graphics g, RowItem r)
        {
            RectangleF R = r.R;
            if (r.Hover && !r.Busy) Fill(g, Inset(R, S(3)), S(6), T.Hover);
            if (!r.Last)
                using (var pen = new Pen(T.Divider, Math.Max(1, S(1))))
                    g.DrawLine(pen, R.X + S(62), R.Bottom, R.Right - S(16), R.Bottom);

            var ic = new RectangleF(R.X + S(14), R.Y + (R.Height - S(36)) / 2, S(36), S(36));
            using (var b = new SolidBrush(Theme.Blend(T.IconOff, T.AccentSoft, r.Anim))) g.FillEllipse(b, ic);
            Glyph(g, r.Glyph, fGlyph, Theme.Blend(T.Sub, T.Accent, r.Anim), ic);

            float tx = R.X + S(62), tw = R.Right - S(r.NoToggle ? 84 : 72) - tx;
            Text_(g, r.Title, fRow, T.Text, new RectangleF(tx, R.Y + S(10), tw, S(22)));
            Text_(g, r.Busy ? "正在应用…" : r.Detail, fSmall, r.Busy ? T.Accent : T.Sub, new RectangleF(tx, R.Y + S(32), tw, S(18)));

            if (!r.NoToggle)
                DrawToggle(g, new RectangleF(R.Right - S(16) - S(40), R.Y + (R.Height - S(20)) / 2, S(40), S(20)), r.Anim, r.Busy, r.Hover);
        }

        void DrawToggle(Graphics g, RectangleF r, float t, bool busy, bool hover)
        {
            int a = busy ? 110 : 255;
            using (var path = Round(r, r.Height / 2))
            {
                if (t < 1)
                    using (var pen = new Pen(Color.FromArgb((int)(a * (1 - t)), T.ToggleOff), Math.Max(1, S(1))))
                    using (var inner = Round(Inset(r, S(0.5f)), r.Height / 2 - S(0.5f)))
                        g.DrawPath(pen, inner);
                if (t > 0)
                    using (var b = new SolidBrush(Color.FromArgb((int)(a * t), T.Accent)))
                        g.FillPath(b, path);
            }
            float d = S(12) + S(2) * t + (hover && !busy ? S(1) : 0);
            float cx = r.X + S(10) + (r.Width - S(20)) * t;
            float cy = r.Y + r.Height / 2;
            using (var b = new SolidBrush(Color.FromArgb(a, Theme.Blend(T.ToggleOff, T.KnobOn, t))))
                g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
        }

        void DrawLink(Graphics g, LinkItem l)
        {
            Color c = l.Accent ? T.Accent : T.Text;
            if (l.Filled)
            {
                Fill(g, l.R, S(6), l.Hover ? Theme.Blend(T.Accent, T.Dark ? Color.White : Color.Black, 0.1f) : T.Accent);
                c = T.Dark ? Color.Black : Color.White;
            }
            else if (l.Hover)
                Fill(g, l.R, S(6), T.Dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(12, 0, 0, 0));

            if (l.Text == null)
            {
                Glyph(g, l.Glyph, fGlyphSmall, c, l.R);
                return;
            }
            if (l.Glyph == null)
            {
                TextRenderer.DrawText(g, l.Text, fSmall, Rect(l.R), c,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }
            var gr = new RectangleF(l.R.X + S(8), l.R.Y, S(20), l.R.Height);
            Glyph(g, l.Glyph, fGlyphSmall, c, gr);
            Text_(g, l.Text, fSmall, c, new RectangleF(gr.Right + S(6), l.R.Y, l.R.Right - gr.Right - S(6), l.R.Height));
        }

        static void Text_(Graphics g, string text, Font f, Color c, RectangleF r)
        {
            TextRenderer.DrawText(g, text, f, Rect(r), c,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        static void Glyph(Graphics g, string glyph, Font f, Color c, RectangleF r)
        {
            TextRenderer.DrawText(g, glyph, f, Rect(r), c,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        static void Fill(Graphics g, RectangleF r, float radius, Color c)
        {
            using (var p = Round(r, radius))
            using (var b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        static Rectangle Rect(RectangleF r)
        {
            return Rectangle.Round(r);
        }

        static RectangleF Inset(RectangleF r, float d)
        {
            return new RectangleF(r.X + d, r.Y + d, r.Width - 2 * d, r.Height - 2 * d);
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    class TrayApp : ApplicationContext
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        readonly NotifyIcon tray = new NotifyIcon();
        readonly Flyout flyout = new Flyout();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 10000 };
        IntPtr iconHandle = IntPtr.Zero;

        public TrayApp(bool justInstalled)
        {
            flyout.DevicesChanged += UpdateTray;
            flyout.Installed += OnInstalled;
            flyout.Uninstalled += OnUninstalled;

            var renderer = new MenuRenderer();
            var menu = new ContextMenuStrip { Renderer = renderer, ShowImageMargin = false, Padding = new Padding(4) };
            menu.Items.Add("打开面板", null, (o, e) => flyout.ToggleAt(Cursor.Position));
            menu.Items.Add("退出", null, (o, e) => Application.Exit());
            menu.Opening += (o, e) => renderer.T = Theme.Current(null);
            tray.ContextMenuStrip = menu;
            tray.MouseUp += (o, e) => { if (e.Button == MouseButtons.Left) flyout.ToggleAt(Cursor.Position); };

            UpdateTray(new List<TouchDevice>());
            tray.Visible = true;
            if (justInstalled)
                tray.ShowBalloonTip(4000, "触摸设备", "后台助手已安装，点击托盘图标即可开关触摸设备。", ToolTipIcon.Info);

            timer.Tick += (o, e) => { if (!flyout.Visible) flyout.Reload(false); };
            timer.Start();
            flyout.Reload();
        }

        void OnInstalled()
        {
            if (AutoStart.IsEnabled()) AutoStart.Set(true);   // 指向安装后的位置
            if (Helper.RunningInstalled) return;
            // 改由 Program Files 中的副本继续运行
            try { Process.Start(new ProcessStartInfo(Helper.InstalledExe, "--relaunch --installed") { UseShellExecute = false }); }
            catch (Exception) { flyout.Reload(); return; }
            Application.Exit();
        }

        void OnUninstalled()
        {
            if (Helper.RunningInstalled)
            {
                // 安装目录即将被删除
                AutoStart.Set(false);
                Application.Exit();
            }
            else if (AutoStart.IsEnabled()) AutoStart.Set(true, Application.ExecutablePath);
        }

        void UpdateTray(List<TouchDevice> devices)
        {
            var screens = devices.FindAll(d => d.Kind == "触摸屏");
            int on = screens.FindAll(d => d.Enabled).Count;
            Color c = screens.Count == 0 ? Color.Gray
                    : on == screens.Count ? Color.FromArgb(46, 160, 67)
                    : on == 0 ? Color.FromArgb(207, 34, 46)
                    : Color.FromArgb(219, 140, 0);

            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var path = Flyout.Round(new RectangleF(1, 3, 30, 21), 4))
                using (var fill = new SolidBrush(c))
                    g.FillPath(fill, path);
                using (var stand = new SolidBrush(c))
                    g.FillRectangle(stand, 10, 26, 12, 4);
                using (var white = new SolidBrush(Color.White))
                    g.FillEllipse(white, 10, 7, 12, 12);
                if (screens.Count > 0 && on == 0)
                    using (var pen = new Pen(c, 3))
                        g.DrawLine(pen, 8, 21, 24, 5);

                IntPtr h = bmp.GetHicon();
                Icon old = tray.Icon;
                tray.Icon = Icon.FromHandle(h);
                if (old != null) old.Dispose();
                if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
                iconHandle = h;
            }

            string text = screens.Count == 0 ? "触摸设备：未检测到触摸屏" : "触摸屏 " + on + "/" + screens.Count + " 已开启";
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        protected override void ExitThreadCore()
        {
            timer.Stop();
            tray.Visible = false;
            tray.Dispose();
            if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
            base.ExitThreadCore();
        }
    }

    // 右键菜单配色与面板一致
    class MenuRenderer : ToolStripProfessionalRenderer
    {
        public Theme T = Theme.Current(null);

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(T.Bg)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var pen = new Pen(T.Border))
                e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Flyout.Round(new RectangleF(2, 1, e.Item.Width - 4, e.Item.Height - 2), 4))
            using (var b = new SolidBrush(T.Hover))
                e.Graphics.FillPath(b, p);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = T.Text;
            base.OnRenderItemText(e);
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0] : "";
            if (mode == "--helper") return Helper.Serve();
            if (mode == "--install") return args.Length > 1 ? Helper.DoInstall(args[1]) : 1;
            if (mode == "--uninstall") return Helper.DoUninstall();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 调试用：TouchToggle.exe --snapshot out.png [dark|light] [缩放]，把面板渲染成图片
            if (mode == "--snapshot" && args.Length >= 2)
            {
                var f = new Flyout();
                bool? dark = args.Length > 2 ? (bool?)(args[2] == "dark") : null;
                f.ApplyTheme(dark, args.Length > 3 ? float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 1.5f);
                f.LoadNow();
                using (var bmp = f.Snapshot()) bmp.Save(args[1], ImageFormat.Png);
                return 0;
            }

            bool relaunch = Array.IndexOf(args, "--relaunch") >= 0;
            using (var mutex = new Mutex(false, "Local\\TouchToggleTray"))
            {
                bool owned;
                try { owned = mutex.WaitOne(relaunch ? 5000 : 0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) return 0;
                try { Application.Run(new TrayApp(Array.IndexOf(args, "--installed") >= 0)); }
                finally { mutex.ReleaseMutex(); }
            }
            return 0;
        }
    }
}
