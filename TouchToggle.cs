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
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("TouchToggle")]
[assembly: AssemblyDescription("触摸设备开关与屏幕亮度托盘工具")]
[assembly: AssemblyProduct("TouchToggle")]
[assembly: AssemblyCompany("willlllliams")]
[assembly: AssemblyCopyright("Copyright © 2026 TouchToggle contributors · MIT License")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]
[assembly: AssemblyInformationalVersion("1.2.0")]

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

        // 屏幕（\\.\DISPLAY1）上的显示器接口路径，如 \\?\DISPLAY#AUOA48F#4&33fd3a7c&0&UID8388688#{guid}
        public static List<string> MonitorPaths(string screenDeviceName)
        {
            var paths = new List<string>();
            var dd = new DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(dd);
            for (uint i = 0; EnumDisplayDevices(screenDeviceName, i, ref dd, 1); i++)   // 1 = EDD_GET_DEVICE_INTERFACE_NAME
            {
                if (!string.IsNullOrEmpty(dd.DeviceID)) paths.Add(dd.DeviceID);
                dd.cb = Marshal.SizeOf(dd);
            }
            return paths;
        }

        // 触摸设备实例 ID → { "主屏幕"/"副屏幕", "1920×1080" }，依据“平板电脑设置”里的屏幕校准映射
        static Dictionary<string, string[]> TouchToScreen()
        {
            var monitors = new Dictionary<string, string[]>();
            foreach (Screen sc in Screen.AllScreens)
                foreach (string path in MonitorPaths(sc.DeviceName))
                    monitors[path.ToUpperInvariant()] = new[]
                    {
                        sc.Primary ? "主屏幕" : "副屏幕",
                        sc.Bounds.Width + "×" + sc.Bounds.Height,
                    };

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

        // 助手写回的状态（ScreenPad 亮度）。所在目录只有 SYSTEM 和管理员可写，用户只读
        static string StateFile { get { return Path.Combine(DataDir, "state.txt"); } }

        // 从别处运行的新版本与已安装的后台助手不同，需要重新安装（更新）
        public static bool NeedsUpdate()
        {
            if (RunningInstalled || !File.Exists(InstalledExe)) return false;
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] a, b;
                    using (var f = File.OpenRead(Application.ExecutablePath)) a = sha.ComputeHash(f);
                    using (var f = File.OpenRead(InstalledExe)) b = sha.ComputeHash(f);
                    return Convert.ToBase64String(a) != Convert.ToBase64String(b);
                }
            }
            catch (Exception) { return false; }
        }

        public static bool SamePath(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }

        public static bool RunningInstalled { get { return SamePath(Application.ExecutablePath, InstalledExe); } }

        // 用安装程序（Inno Setup）安装时生成的卸载程序；用面板安装时为 null
        public static string SetupUninstaller
        {
            get
            {
                string p = Path.Combine(InstallDir, "unins000.exe");
                return File.Exists(p) ? p : null;
            }
        }

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)]
        static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);
        [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr p);

        static string SessionInfo(int sessionId, int infoClass)
        {
            IntPtr buf;
            int bytes;
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out buf, out bytes)) return null;
            try { return Marshal.PtrToStringUni(buf); }
            finally { WTSFreeMemory(buf); }
        }

        // 当前会话里登录的用户（域\用户名）。UAC 用另一个管理员账户授权时，进程身份是那个管理员，
        // 但应当把权限授予实际坐在电脑前的用户
        public static string SessionUser()
        {
            int session = Process.GetCurrentProcess().SessionId;
            string user = SessionInfo(session, 5);     // WTSUserName
            string domain = SessionInfo(session, 7);   // WTSDomainName
            if (string.IsNullOrEmpty(user)) return Environment.UserDomainName + "\\" + Environment.UserName;
            return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
        }

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

        // 设置 ScreenPad 背光原始值（20-255），raw < 0 表示只查询。
        // 返回助手读到的当前值；-1 = 没有 ScreenPad；-2 = 助手不可用或超时
        public static int ScreenPad(int raw)
        {
            lock (requestLock)
            {
                DateTime start = DateTime.UtcNow;
                string req = Path.Combine(QueueDir, Guid.NewGuid().ToString("N") + ".req");
                try { File.WriteAllText(req, raw < 0 ? "Q\r\n" : "B " + raw + "\r\n", Encoding.UTF8); }
                catch (Exception) { return -2; }
                try
                {
                    string o;
                    if (Shell.Run(Shell.Sys("schtasks.exe"), "/run /tn " + TaskName, out o) != 0) return -2;
                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 8000)
                    {
                        Thread.Sleep(80);
                        var fi = new FileInfo(StateFile);
                        if (!fi.Exists || fi.LastWriteTimeUtc < start) continue;
                        int v = ReadState();
                        if (v != -2) return v;
                    }
                    return -2;
                }
                finally
                {
                    try { File.Delete(req); } catch (Exception) { }
                }
            }
        }

        static int ReadState()
        {
            try
            {
                string t = File.ReadAllText(StateFile, Encoding.ASCII).Trim();
                int v;
                if (t.StartsWith("screenpad=") && int.TryParse(t.Substring(10), out v)) return v;
            }
            catch (Exception) { }
            return -2;
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
                    "  <RegistrationInfo><Description>TouchToggle 后台助手：按托盘程序的请求启用/禁用触摸设备、调节 ScreenPad 亮度</Description>" +
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

        // keepFiles：由安装程序的卸载程序调用时为 true，程序文件交给它删除
        public static int DoUninstall(bool keepFiles)
        {
            string o;
            Shell.Run(Shell.Sys("schtasks.exe"), "/delete /tn " + TaskName + " /f", out o);
            Shell.Run(Shell.Sys("schtasks.exe"), "/delete /tn " + LegacyTaskName + " /f", out o);
            try { if (Directory.Exists(DataDir)) Directory.Delete(DataDir, true); } catch (Exception) { }
            if (keepFiles) return 0;
            // 等托盘程序退出后再删除安装目录
            var psi = new ProcessStartInfo(Shell.Sys("cmd.exe"),
                "/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"" + InstallDir + "\"")
            { UseShellExecute = false, CreateNoWindow = true };
            Process.Start(psi);
            return 0;
        }

        // 由计划任务以 SYSTEM 身份启动：执行队列中的请求。对队列只读；只接受当前枚举到的触摸类设备；
        // 忽略两分钟前的旧请求（托盘程序异常退出时可能残留）。请求格式（每行一条）：
        //   1 <设备实例ID> / 0 <设备实例ID>   启用 / 禁用触摸设备
        //   B <20-255>                        设置 ScreenPad 背光
        //   Q                                 只查询 ScreenPad 背光
        // 处理完后把 ScreenPad 背光写入 state.txt（管理员所有的目录）。
        public static int Serve()
        {
            string dir = QueueDir;
            if (!Directory.Exists(dir) || IsLink(dir) || IsLink(DataDir)) return 0;
            List<TouchDevice> devices = null;
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
                        int raw;
                        if (line.StartsWith("B ") && int.TryParse(line.Substring(2).Trim(), out raw))
                        {
                            try { Atk.SetScreenPad(raw); } catch (Exception) { }
                            continue;
                        }
                        if (line.Length < 3 || (line[0] != '0' && line[0] != '1') || line[1] != ' ') continue;
                        string id = line.Substring(2).Trim();
                        if (devices == null) devices = Devices.Load();
                        TouchDevice d = devices.Find(x => string.Equals(x.InstanceId, id, StringComparison.OrdinalIgnoreCase));
                        if (d != null && d.Enabled != (line[0] == '1')) Devices.SetEnabled(d, line[0] == '1');
                    }
                }
                catch (Exception) { }
            }

            try
            {
                int sp = -1;
                try { sp = Atk.GetScreenPad(); } catch (Exception) { }
                string tmp = Path.Combine(DataDir, "state.tmp");
                File.WriteAllText(tmp, "screenpad=" + sp, Encoding.ASCII);
                if (File.Exists(StateFile)) File.Replace(tmp, StateFile, null);
                else File.Move(tmp, StateFile);
            }
            catch (Exception) { }
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

    enum BrightnessKind { Wmi, Ddc, ScreenPad }

    class Display
    {
        public string Key, Name, Detail;
        public BrightnessKind Kind;
        public int Value = -1;          // 0-100，界面上显示的值
        public string WmiInstance;      // Wmi
        public string DeviceName;       // Ddc：\\.\DISPLAY2
        public int PhysicalIndex;       // Ddc：同一个 HMONITOR 下的第几个物理显示器
        public uint DdcMin, DdcMax;

        // 以下由 lock(this) 保护：界面线程写 Pending，后台线程把它应用到硬件
        public int Pending, Applied;
        public bool Applying;
    }

    // 屏幕亮度，参考 Monitorian：
    //   笔记本内置屏 → WMI (WmiMonitorBrightness)，普通权限即可
    //   外接显示器   → DDC/CI (dxva2)，普通权限即可
    //   华硕 ScreenPad → ASUS ATK ACPI WMI，需要管理员，由后台助手代为执行
    static class Brightness
    {
        // ScreenPad 背光原始值范围。低于 20 时背光实际已不可见（与 Linux asus-wmi 驱动一致）
        public const int ScreenPadMin = 20, ScreenPadMax = 255;

        public static int FromScreenPadRaw(int raw)
        {
            return Math.Max(0, Math.Min(100, (int)Math.Round((raw - ScreenPadMin) * 100.0 / (ScreenPadMax - ScreenPadMin))));
        }

        public static int ToScreenPadRaw(int value)
        {
            return ScreenPadMin + (int)Math.Round(value * (ScreenPadMax - ScreenPadMin) / 100.0);
        }

        // screenPadRaw：后台助手读到的 ScreenPad 背光值，-1 表示没有 ScreenPad 或未知
        public static List<Display> Load(int screenPadRaw)
        {
            var wmi = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, CurrentBrightness FROM WmiMonitorBrightness"))
                    foreach (ManagementObject mo in s.Get())
                        wmi[(string)mo["InstanceName"]] = Convert.ToInt32(mo["CurrentBrightness"]);
            }
            catch (Exception) { }   // 没有支持亮度的屏幕时 WMI 会报“不支持”

            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, UserFriendlyName FROM WmiMonitorID"))
                    foreach (ManagementObject mo in s.Get())
                    {
                        var chars = mo["UserFriendlyName"] as ushort[];
                        if (chars == null) continue;
                        var sb = new StringBuilder();
                        foreach (ushort c in chars) if (c != 0) sb.Append((char)c);
                        if (sb.Length > 0) names[(string)mo["InstanceName"]] = sb.ToString();
                    }
            }
            catch (Exception) { }

            var screens = new List<Screen>(Screen.AllScreens);
            screens.Sort((a, b) => a.Primary != b.Primary ? (a.Primary ? -1 : 1)
                                 : a.Bounds.Y != b.Bounds.Y ? a.Bounds.Y.CompareTo(b.Bounds.Y) : a.Bounds.X.CompareTo(b.Bounds.X));

            var list = new List<Display>();
            var rest = new List<KeyValuePair<Screen, string>>();   // 不支持 WMI 亮度的屏幕及其 WMI 实例名
            foreach (Screen sc in screens)
            {
                foreach (string path in Devices.MonitorPaths(sc.DeviceName))
                {
                    string inst = ToWmiInstance(path);
                    int v;
                    if (inst != null && wmi.TryGetValue(inst, out v))
                        list.Add(new Display
                        {
                            Key = "wmi:" + inst, Kind = BrightnessKind.Wmi, WmiInstance = inst, Value = v,
                            Name = sc.Primary ? "主屏幕" : NameOf(inst, names),
                            Detail = Res(sc) + " · 系统",
                        });
                    else rest.Add(new KeyValuePair<Screen, string>(sc, inst));
                }
            }

            // DDC/CI：外接显示器
            var noDdc = new List<KeyValuePair<Screen, string>>(rest);
            if (rest.Count > 0)
            {
                ForEachPhysical((dev, index, h) =>
                {
                    var hit = rest.Find(p => string.Equals(p.Key.DeviceName, dev, StringComparison.OrdinalIgnoreCase));
                    if (hit.Key == null) return;
                    uint min, cur, max;
                    if (!GetMonitorBrightness(h, out min, out cur, out max) || max <= min) return;
                    noDdc.RemoveAll(p => p.Key == hit.Key);
                    list.Add(new Display
                    {
                        Key = "ddc:" + dev + "#" + index, Kind = BrightnessKind.Ddc, DeviceName = dev, PhysicalIndex = index,
                        DdcMin = min, DdcMax = max, Value = (int)Math.Round((cur - min) * 100.0 / (max - min)),
                        Name = hit.Key.Primary ? "主屏幕" : NameOf(hit.Value, names),
                        Detail = Res(hit.Key) + " · DDC/CI",
                    });
                });
            }

            // ScreenPad：剩下的屏幕中优先选超宽的那块（ScreenPad Plus 为 1920×515）
            if (screenPadRaw >= 0 && noDdc.Count > 0)
            {
                noDdc.Sort((a, b) => Ratio(b.Key).CompareTo(Ratio(a.Key)));
                var sp = noDdc[0];
                if (noDdc.Count == 1 || Ratio(sp.Key) >= 3)
                    list.Add(new Display
                    {
                        Key = "screenpad", Kind = BrightnessKind.ScreenPad, Value = FromScreenPadRaw(screenPadRaw),
                        Name = "ScreenPad 副屏", Detail = Res(sp.Key) + " · 华硕",
                    });
            }

            foreach (var d in list) { d.Pending = d.Value; d.Applied = d.Value; }
            return list;
        }

        // 返回是否成功；在后台线程调用
        public static bool Set(Display d, int value)
        {
            value = Math.Max(0, Math.Min(100, value));
            switch (d.Kind)
            {
                case BrightnessKind.Wmi:
                    using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods"))
                        foreach (ManagementObject mo in s.Get())
                            if (string.Equals((string)mo["InstanceName"], d.WmiInstance, StringComparison.OrdinalIgnoreCase))
                            {
                                mo.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)value });
                                return true;
                            }
                    return false;

                case BrightnessKind.Ddc:
                    bool ok = false;
                    ForEachPhysical((dev, index, h) =>
                    {
                        if (index == d.PhysicalIndex && string.Equals(dev, d.DeviceName, StringComparison.OrdinalIgnoreCase))
                            ok = SetMonitorBrightness(h, (uint)(d.DdcMin + Math.Round((d.DdcMax - d.DdcMin) * value / 100.0)));
                    });
                    return ok;

                default:
                    return Helper.ScreenPad(ToScreenPadRaw(value)) >= 0;
            }
        }

        // \\?\DISPLAY#AUOA48F#4&33fd3a7c&0&UID8388688#{guid} → DISPLAY\AUOA48F\4&33fd3a7c&0&UID8388688_0
        static string ToWmiInstance(string path)
        {
            int start = path.IndexOf(@"\\?\", StringComparison.Ordinal);
            int end = path.LastIndexOf("#{", StringComparison.Ordinal);
            if (start < 0 || end <= start) return null;
            return path.Substring(start + 4, end - start - 4).Replace('#', '\\') + "_0";
        }

        static string NameOf(string inst, Dictionary<string, string> names)
        {
            string n;
            return inst != null && names.TryGetValue(inst, out n) ? n : "显示器";
        }

        static string Res(Screen sc) { return sc.Bounds.Width + "×" + sc.Bounds.Height; }

        static double Ratio(Screen sc) { return (double)sc.Bounds.Width / Math.Max(1, sc.Bounds.Height); }

        // ---- DDC/CI ----

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PHYSICAL_MONITOR
        {
            public IntPtr Handle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor, rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
        [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
        [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
        [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);
        [DllImport("dxva2.dll")] static extern bool GetMonitorBrightness(IntPtr h, out uint min, out uint cur, out uint max);
        [DllImport("dxva2.dll")] static extern bool SetMonitorBrightness(IntPtr h, uint value);

        // 依次打开每个物理显示器的句柄交给 f，用完立即释放
        static void ForEachPhysical(Action<string, int, IntPtr> f)
        {
            var monitors = new List<IntPtr>();
            MonitorEnumProc proc = (h, dc, r, data) => { monitors.Add(h); return true; };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
            GC.KeepAlive(proc);

            foreach (IntPtr hMon in monitors)
            {
                var info = new MONITORINFOEX();
                info.cbSize = Marshal.SizeOf(info);
                uint n;
                if (!GetMonitorInfo(hMon, ref info) || !GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out n) || n == 0) continue;
                var arr = new PHYSICAL_MONITOR[n];
                if (!GetPhysicalMonitorsFromHMONITOR(hMon, n, arr)) continue;
                try
                {
                    for (int i = 0; i < n; i++) f(info.szDevice, i, arr[i].Handle);
                }
                finally { DestroyPhysicalMonitors(n, arr); }
            }
        }
    }

    // 华硕 ATK ACPI WMI（需要管理员），设备号与 Linux asus-wmi 驱动一致
    static class Atk
    {
        const uint ScreenPadLight = 0x00050032;

        static ManagementObject Instance()
        {
            using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM AsusAtkWmi_WMNB"))
                foreach (ManagementObject mo in s.Get()) return mo;
            return null;
        }

        // 返回 ScreenPad 背光原始值 0-255；没有 ScreenPad 返回 -1
        public static int GetScreenPad()
        {
            var mo = Instance();
            if (mo == null) return -1;
            var args = mo.GetMethodParameters("DSTS");
            args["Device_ID"] = ScreenPadLight;
            uint status = Convert.ToUInt32(mo.InvokeMethod("DSTS", args, null)["device_status"]);
            if ((status & 0x00010000) == 0) return -1;   // 0x10000 = 设备存在
            return (int)(status & 0xFF);
        }

        public static void SetScreenPad(int raw)
        {
            var mo = Instance();
            if (mo == null) return;
            var args = mo.GetMethodParameters("DEVS");
            args["Device_ID"] = ScreenPadLight;
            args["Control_status"] = (uint)Math.Max(Brightness.ScreenPadMin, Math.Min(Brightness.ScreenPadMax, raw));
            mo.InvokeMethod("DEVS", args, null);
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

    class SliderItem : Item
    {
        public Display D;
        public RectangleF Track;   // 可拖动区域
        public bool Last;
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
                     GlyphRefresh = "\uE72C", GlyphPower = "\uE7E8", GlyphStartup = "\uE7B5",
                     GlyphBrightness = "\uE706", GlyphLink = "\uE71B";
        const string SettingsKey = @"Software\TouchToggle";

        public event Action<List<TouchDevice>> DevicesChanged;
        public event Action Installed;
        public event Action Uninstalled;

        Theme T;
        float s = 1;
        Font fTitle, fSub, fRow, fSmall, fSection, fGlyph, fGlyphSmall;

        List<TouchDevice> devices = new List<TouchDevice>();
        List<Display> displays = new List<Display>();
        int screenPadRaw = -1;
        bool autoStart, installed = true, needsUpdate, installing, unison;
        string error;
        SliderItem dragging;
        readonly LinkItem unisonBtn = new LinkItem { Glyph = GlyphLink, Text = "联动" };
        readonly Dictionary<string, RowItem> rows = new Dictionary<string, RowItem>();
        readonly List<RowItem> drawRows = new List<RowItem>();
        readonly List<SliderItem> sliders = new List<SliderItem>();
        readonly List<Item> clickables = new List<Item>();
        readonly List<KeyValuePair<RectangleF, string>> sections = new List<KeyValuePair<RectangleF, string>>();
        readonly List<RectangleF> cards = new List<RectangleF>();
        readonly LinkItem refreshBtn = new LinkItem { Glyph = GlyphRefresh };
        readonly LinkItem exitBtn = new LinkItem { Glyph = GlyphPower, Text = "退出" };
        readonly LinkItem installBtn = new LinkItem { Text = "立即安装", Filled = true };
        readonly LinkItem uninstallBtn = new LinkItem { Text = "卸载" };
        RectangleF errorRect, installRect, footerRect, viewRect;
        Item hovered;
        float scroll, scrollMax;
        Size layoutSize;         // 布局算出的尺寸（窗口可能被系统限制得比它小，截图按它来画）
        public bool Unbounded;   // 截图用：不限制高度，不滚动

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
            unison = ReadSetting("Unison") == 1;
            unisonBtn.Click = () =>
            {
                unison = !unison;
                WriteSetting("Unison", unison ? 1 : 0);
                LayoutItems();
            };
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

        // full 为 false 时只刷新触摸设备（用于后台定时刷新托盘图标）；
        // 为 true 时还会查询安装状态、屏幕亮度，并让后台助手读取 ScreenPad 亮度
        public void Reload(bool full)
        {
            List<TouchDevice> list = null;
            List<Display> disp = null;
            bool auto = autoStart, inst = installed, update = needsUpdate;
            int sp = screenPadRaw;
            Background(() =>
            {
                list = Devices.Load();
                auto = AutoStart.IsEnabled();
                if (!full) return;
                inst = Helper.IsInstalled();
                update = inst && Helper.NeedsUpdate();
                disp = Brightness.Load(sp);
            }, () =>
            {
                if (list == null) error = "读取设备列表失败";
                else SetDevices(list);
                autoStart = auto;
                installed = inst;
                needsUpdate = update;
                if (disp != null) MergeDisplays(disp);
                LayoutItems();
                if (full && installed && !needsUpdate) RefreshScreenPad();
            });
        }

        // ScreenPad 亮度只能由后台助手读取，单独异步进行，不拖慢面板打开
        void RefreshScreenPad()
        {
            int raw = -2;
            List<Display> disp = null;
            Background(() =>
            {
                raw = Helper.ScreenPad(-1);
                if (raw != -2) disp = Brightness.Load(raw);
            }, () =>
            {
                if (raw == -2) return;
                screenPadRaw = raw;
                MergeDisplays(disp);
                LayoutItems();
            });
        }

        // 用新读到的列表更新显示器；正在拖动或正在写入的保留界面上的值，避免滑块跳回去
        void MergeDisplays(List<Display> fresh)
        {
            var merged = new List<Display>();
            foreach (var f in fresh)
            {
                var old = displays.Find(d => d.Key == f.Key);
                if (old == null) { merged.Add(f); continue; }
                old.Name = f.Name;
                old.Detail = f.Detail;
                old.DeviceName = f.DeviceName;
                old.PhysicalIndex = f.PhysicalIndex;
                old.DdcMin = f.DdcMin;
                old.DdcMax = f.DdcMax;
                lock (old)
                {
                    bool busy = old.Applying || (dragging != null && dragging.D == old);
                    if (!busy) { old.Value = f.Value; old.Pending = f.Value; old.Applied = f.Value; }
                }
                merged.Add(old);
            }
            displays = merged;
        }

        // 界面上改了亮度：立即更新显示，硬件写入在后台合并进行（拖动时只写最新值）
        void SetBrightness(Display d, int value)
        {
            value = Math.Max(0, Math.Min(100, value));
            if (d.Value < 0 || value == d.Value) return;
            d.Value = value;
            lock (d)
            {
                d.Pending = value;
                if (d.Applying) return;
                d.Applying = true;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool failed = false;
                while (true)
                {
                    int target;
                    lock (d)
                    {
                        if (d.Pending == d.Applied) { d.Applying = false; break; }
                        target = d.Pending;
                    }
                    bool ok = false;
                    try { ok = Brightness.Set(d, target); }
                    catch (Exception ex) { Debug.WriteLine(ex); }
                    if (!ok) failed = true;
                    lock (d) d.Applied = target;
                }
                if (failed)
                    Post(() =>
                    {
                        error = d.Kind == BrightnessKind.ScreenPad && !installed ? "调节 ScreenPad 亮度需要先安装后台助手" : d.Name + "：调节亮度失败";
                        LayoutItems();
                    });
            });
        }

        // 拖动一个滑块；开启联动时其他屏幕按相同幅度一起变化
        void ChangeBrightness(SliderItem s, int value)
        {
            int delta = Math.Max(0, Math.Min(100, value)) - s.D.Value;
            if (delta == 0) return;
            if (unison)
                foreach (var d in displays)
                    if (d != s.D) SetBrightness(d, d.Value + delta);
            SetBrightness(s.D, s.D.Value + delta);
            Invalidate();
        }

        // 在后台线程执行 work，再回到界面线程执行 done；异常不会让程序崩溃，程序退出后不再回调
        void Background(Action work, Action done)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { work(); }
                catch (Exception ex) { Debug.WriteLine(ex); }
                Post(done);
            });
        }

        void Post(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }
            catch (InvalidOperationException) { }   // 程序正在退出
        }

        static int ReadSetting(string name)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
                {
                    object v = k == null ? null : k.GetValue(name);
                    return v is int ? (int)v : 0;
                }
            }
            catch (Exception) { return 0; }
        }

        static void WriteSetting(string name, int value)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey)) k.SetValue(name, value, RegistryValueKind.DWord);
            }
            catch (Exception) { }
        }

        // 截图模式用：同步加载，不检查是否需要更新（截图程序本身总是与已安装的版本不同）
        public void LoadNow()
        {
            SetDevices(Devices.Load());
            autoStart = AutoStart.IsEnabled();
            installed = Helper.IsInstalled();
            MergeDisplays(Brightness.Load(installed ? Helper.ScreenPad(-1) : -1));
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
            // 用安装程序安装的：交给正式的卸载程序（它会自己请求管理员权限并关闭本程序）
            string uninstaller = Helper.SetupUninstaller;
            if (uninstaller != null)
            {
                HideFlyout();
                try { Process.Start(new ProcessStartInfo(uninstaller) { UseShellExecute = true }); }
                catch (Exception) { error = "无法启动卸载程序"; }
                return;
            }
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

            // 标题和底栏固定，中间内容超出屏幕高度时可滚动；中间部分的坐标直接减去滚动量
            float viewTop = y;
            y -= scroll;

            if (error != null)
            {
                errorRect = new RectangleF(pad, y, W - 2 * pad, S(40));
                y += S(50);
            }
            else errorRect = RectangleF.Empty;

            if (!installed || needsUpdate)
            {
                installRect = new RectangleF(pad, y, W - 2 * pad, S(120));
                installBtn.R = new RectangleF(installRect.Right - S(16) - S(104), installRect.Bottom - S(16) - S(32), S(104), S(32));
                if (!installing) installBtn.Text = needsUpdate ? "立即更新" : "立即安装";
                clickables.Add(installBtn);
                y += installRect.Height + S(16);
            }
            else installRect = RectangleF.Empty;

            // 亮度
            sliders.Clear();
            if (displays.Count > 0)
            {
                var sec = new RectangleF(S(20), y, W - S(40), S(22));
                sections.Add(new KeyValuePair<RectangleF, string>(sec, "亮度"));
                if (displays.Count > 1)
                {
                    unisonBtn.Accent = unison;
                    unisonBtn.R = new RectangleF(sec.Right - S(68), sec.Y - S(4), S(72), S(28));
                    clickables.Add(unisonBtn);
                }
                y += S(26);
                float sliderH = S(70);
                cards.Add(new RectangleF(pad, y, W - 2 * pad, displays.Count * sliderH));
                for (int i = 0; i < displays.Count; i++)
                {
                    var R = new RectangleF(pad, y + i * sliderH, W - 2 * pad, sliderH);
                    var item = new SliderItem
                    {
                        D = displays[i],
                        R = R,
                        Track = new RectangleF(R.X + S(52), R.Y + S(36), R.Width - S(52) - S(16), S(26)),
                        Last = i == displays.Count - 1,
                    };
                    if (dragging != null && dragging.D == item.D) dragging = item;
                    sliders.Add(item);
                    clickables.Add(item);
                }
                y += displays.Count * sliderH + S(16);
            }

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
                bool viaSetup = Helper.SetupUninstaller != null;
                helper.Detail = needsUpdate ? "需要更新" : "已安装 · v" + Application.ProductVersion;
                helper.Glyph = GlyphShield;
                helper.Last = true;
                helper.R = new RectangleF(setCard.X, y + rowH, setCard.Width, rowH);
                drawRows.Add(helper);
                // 用安装程序安装的，卸载的是整个程序
                uninstallBtn.Text = viaSetup ? "卸载程序" : "卸载";
                float ub = viaSetup ? S(84) : S(56);
                uninstallBtn.R = new RectangleF(setCard.Right - S(12) - ub, helper.R.Y + (rowH - S(30)) / 2, ub, S(30));
                clickables.Add(uninstallBtn);
            }
            y += setCard.Height + S(18);

            float footerH = S(56);
            float full = y + scroll + footerH;
            float maxH = Unbounded ? float.MaxValue : Screen.FromPoint(anchor).WorkingArea.Height - 2 * S(12);
            float height = Math.Min(full, maxH);
            scrollMax = Math.Max(0, full - height);
            if (scroll > scrollMax) { scroll = scrollMax; LayoutItems(); return; }

            viewRect = new RectangleF(0, viewTop, W, height - footerH - viewTop);
            footerRect = new RectangleF(0, height - footerH, W, footerH);
            exitBtn.R = new RectangleF(W - S(16) - S(84), footerRect.Y + S(12), S(84), S(32));
            clickables.Add(exitBtn);
            y = height;

            foreach (var k in new List<string>(rows.Keys))
                if (!live.Contains(k)) rows.Remove(k);
            if (hovered != null && !clickables.Contains(hovered)) hovered = null;

            var size = new Size((int)Math.Ceiling(W), (int)Math.Ceiling(y));
            layoutSize = size;
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
            scroll = 0;
            anchor = cursor;
            ApplyTheme(null, scale);
            Reload();

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
            dragging = null;
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

        int SliderValueAt(SliderItem s, float x)
        {
            float knob = S(10);   // 滑块圆点半径：两端各留出这么多
            float t = (x - s.Track.X - knob) / Math.Max(1, s.Track.Width - 2 * knob);
            return (int)Math.Round(Math.Max(0, Math.Min(1, t)) * 100);
        }

        // 滚动区域外只能点到固定在标题栏、底栏上的按钮
        Item HitTest(Point p)
        {
            bool inView = viewRect.Contains(p);
            return clickables.Find(c => c.R.Contains(p) && (inView || c == refreshBtn || c == exitBtn));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !viewRect.Contains(e.Location)) return;
            var s = sliders.Find(x => x.Track.Contains(e.Location));
            if (s == null || s.D.Value < 0) return;
            dragging = s;
            Capture = true;
            ChangeBrightness(s, SliderValueAt(s, e.X));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            // 触摸板的平滑滚动每次只有几十，累计满一格再处理
            wheelAccum += e.Delta;
            int steps = wheelAccum / 120;
            wheelAccum -= steps * 120;
            if (steps == 0) return;

            // 在亮度行上：调亮度，每格 2%；其他地方：滚动面板
            var s = viewRect.Contains(e.Location) ? sliders.Find(x => x.R.Contains(e.Location)) : null;
            if (s != null && s.D.Value >= 0) ChangeBrightness(s, s.D.Value + steps * 2);
            else if (scrollMax > 0)
            {
                scroll = Math.Max(0, Math.Min(scrollMax, scroll - steps * S(64)));
                LayoutItems();
                hovered = null;
            }
        }

        int wheelAccum;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging != null)
            {
                ChangeBrightness(dragging, SliderValueAt(dragging, e.X));
                return;
            }
            Item hit = HitTest(e.Location);
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
            if (dragging != null)
            {
                dragging = null;
                Capture = false;
                Invalidate();
                return;
            }
            Item hit = HitTest(e.Location);
            if (hit != null && hit.Click != null) hit.Click();
        }

        // ---------- 绘制 ----------

        protected override void OnPaint(PaintEventArgs e) { Render(e.Graphics); }

        public Bitmap Snapshot()
        {
            var bmp = new Bitmap(layoutSize.Width, layoutSize.Height);
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
            Text_(g, "触摸与亮度", fTitle, T.Text, new RectangleF(S(20), S(16), S(260), S(24)));
            string status = total == 0 ? "未检测到触摸屏" : on == total ? "所有触摸屏已开启" : on == 0 ? "所有触摸屏已关闭" : on + " / " + total + " 块触摸屏已开启";
            Text_(g, status, fSub, T.Sub, new RectangleF(S(20), S(40), S(260), S(20)));
            DrawLink(g, refreshBtn);

            g.SetClip(viewRect);
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
                Text_(g, needsUpdate ? "后台助手需要更新" : "首次使用需安装后台助手", fRow, T.Text,
                    new RectangleF(installRect.X + S(46), installRect.Y + S(14), installRect.Width - S(60), S(24)));
                TextRenderer.DrawText(g, needsUpdate
                        ? "当前程序比已安装的版本新。更新需要授权一次管理员权限，请先退出旧版本。"
                        : "只需授权一次管理员权限，之后开关触摸设备、调节 ScreenPad 亮度不再弹出提示。", fSmall,
                    Rect(new RectangleF(installRect.X + S(46), installRect.Y + S(40), installRect.Width - S(60), S(36))), T.Sub,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | Clip);
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

            foreach (var sl in sliders) DrawSlider(g, sl);
            foreach (var r in drawRows) DrawRow(g, r);
            foreach (var c in clickables)
            {
                var l = c as LinkItem;
                if (l != null && l != refreshBtn && l != exitBtn) DrawLink(g, l);
            }
            g.ResetClip();

            // 滚动条
            if (scrollMax > 0)
            {
                float trackY = viewRect.Y + S(4), trackH = viewRect.Height - S(8);
                float thumbH = Math.Max(S(24), trackH * viewRect.Height / (viewRect.Height + scrollMax));
                float thumbY = trackY + (trackH - thumbH) * (scroll / scrollMax);
                Fill(g, new RectangleF(layoutSize.Width - S(7), thumbY, S(3), thumbH), S(1.5f), Color.FromArgb(120, T.Sub));
            }

            // 底栏
            using (var b = new SolidBrush(T.Footer)) g.FillRectangle(b, footerRect);
            using (var pen = new Pen(T.Dark ? Theme.Rgb(44, 44, 44) : Theme.Rgb(224, 224, 224)))
                g.DrawLine(pen, footerRect.X, footerRect.Y, footerRect.Right, footerRect.Y);
            Text_(g, "更改立即生效", fSmall, T.Sub, new RectangleF(S(20), footerRect.Y, S(200), footerRect.Height));
            DrawLink(g, exitBtn);

            // 窗口描边
            using (var p = Round(new RectangleF(0.5f, 0.5f, layoutSize.Width - 1.5f, layoutSize.Height - 1.5f), S(8)))
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

        // 亮度行：图标 + 名称 · 说明 + 数值，下方是 Windows 11 风格的滑块
        void DrawSlider(Graphics g, SliderItem s)
        {
            RectangleF R = s.R;
            Display d = s.D;
            bool active = dragging == s;
            if (!s.Last)
                using (var pen = new Pen(T.Divider, Math.Max(1, S(1))))
                    g.DrawLine(pen, R.X + S(52), R.Bottom, R.Right - S(16), R.Bottom);

            bool known = d.Value >= 0;
            float t = known ? d.Value / 100f : 0;

            // 图标随亮度变亮
            var ic = new RectangleF(R.X + S(12), R.Y + S(12), S(32), S(32));
            using (var b = new SolidBrush(Theme.Blend(T.IconOff, T.AccentSoft, 0.35f + 0.65f * t))) g.FillEllipse(b, ic);
            Glyph(g, GlyphBrightness, fGlyph, Theme.Blend(T.Sub, T.Accent, 0.4f + 0.6f * t), ic);

            float tx = R.X + S(52);
            var valueRect = new RectangleF(R.Right - S(16) - S(44), R.Y + S(12), S(44), S(22));
            TextRenderer.DrawText(g, known ? d.Value.ToString() : "—", fRow, Rect(valueRect), active ? T.Accent : T.Text,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | Clip);

            float nameW = Math.Min(TextRenderer.MeasureText(g, d.Name, fRow, Size.Empty, TextFormatFlags.NoPadding).Width,
                                   valueRect.X - tx - S(8));
            Text_(g, d.Name, fRow, T.Text, new RectangleF(tx, R.Y + S(12), nameW, S(22)));
            Text_(g, d.Detail, fSmall, T.Sub, new RectangleF(tx + nameW + S(8), R.Y + S(13), valueRect.X - tx - nameW - S(16), S(22)));

            // 轨道
            float knobR = S(10);
            float x0 = s.Track.X + knobR, x1 = s.Track.Right - knobR;
            float cy = s.Track.Y + s.Track.Height / 2;
            float cx = x0 + (x1 - x0) * t;
            float th = S(4);
            Fill(g, new RectangleF(x0, cy - th / 2, x1 - x0, th), th / 2,
                Color.FromArgb(T.Dark ? 140 : 120, T.ToggleOff));
            if (known && cx > x0)
                Fill(g, new RectangleF(x0, cy - th / 2, cx - x0, th), th / 2, T.Accent);

            // 圆点：外圈 + 强调色内点，悬停变大、按下变小
            if (!known) return;
            using (var b = new SolidBrush(T.Dark ? Theme.Rgb(69, 69, 69) : Color.White))
                g.FillEllipse(b, cx - knobR, cy - knobR, knobR * 2, knobR * 2);
            using (var pen = new Pen(T.Dark ? Theme.Rgb(80, 80, 80) : Theme.Rgb(218, 218, 218), Math.Max(1, S(1))))
                g.DrawEllipse(pen, cx - knobR, cy - knobR, knobR * 2, knobR * 2);
            float inner = active ? S(4.5f) : s.Hover ? S(7) : S(6);
            using (var b = new SolidBrush(T.Accent))
                g.FillEllipse(b, cx - inner, cy - inner, inner * 2, inner * 2);
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
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | Clip);
                return;
            }
            var gr = new RectangleF(l.R.X + S(8), l.R.Y, S(20), l.R.Height);
            Glyph(g, l.Glyph, fGlyphSmall, c, gr);
            Text_(g, l.Text, fSmall, c, new RectangleF(gr.Right + S(6), l.R.Y, l.R.Right - gr.Right - S(6), l.R.Height));
        }

        // TextRenderer 走 GDI，默认不遵守 Graphics 的裁剪区域，所有文字都要带上这个标志
        const TextFormatFlags Clip = TextFormatFlags.PreserveGraphicsClipping;

        static void Text_(Graphics g, string text, Font f, Color c, RectangleF r)
        {
            TextRenderer.DrawText(g, text, f, Rect(r), c,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | Clip);
        }

        static void Glyph(Graphics g, string glyph, Font f, Color c, RectangleF r)
        {
            TextRenderer.DrawText(g, glyph, f, Rect(r), c,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | Clip);
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
            if (mode == "--install") return Helper.DoInstall(args.Length > 1 ? args[1] : Helper.SessionUser());
            if (mode == "--uninstall") return Helper.DoUninstall(Array.IndexOf(args, "--keep-files") >= 0);
            // 安装程序以当前用户身份调用，设置开机自启动
            if (mode == "--autostart") return AutoStart.Set(args.Length > 1 && args[1] == "on") == null ? 0 : 1;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 调试用：TouchToggle.exe --snapshot out.png [dark|light] [缩放]，把面板渲染成图片
            if (mode == "--snapshot" && args.Length >= 2)
            {
                var f = new Flyout { Unbounded = args.Length <= 4 || args[4] != "bounded" };
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
