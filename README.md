# TouchToggle

[中文](#中文) · [English](#english)

<p align="center"><img src="docs/screenshot.png" width="360" alt="TouchToggle 面板 / panel"></p>

---

## 中文

一个常驻 Windows 托盘的小工具，可以**逐个开启或关闭**电脑上的触摸屏、手写笔和触摸板。特别适合双屏笔记本（如 ASUS ZenBook Duo），可以只关掉某一块屏幕的触摸。

### 功能

- **按设备开关**：触摸屏、手写笔、触摸板分组显示，每个设备一个开关；同类有多个时可一键“全部开启 / 全部关闭”。
- **自动识别屏幕**：根据 Windows“平板电脑设置”中的校准信息，标出每个触摸设备对应**主屏幕**还是**副屏幕**及其分辨率。
- **精致的面板**：仿 Windows 快速设置的弹出面板，跟随系统深色 / 浅色模式与强调色，开关带动画，支持高 DPI。
- **托盘图标显示状态**：绿色 = 触摸屏全部开启，橙色 = 部分开启，红色带斜线 = 全部关闭。
- **只需一次管理员授权**：程序本身以普通权限运行，安装时授权一次，之后开关设备不再弹出 UAC。
- **开机自启动**：一键开启，写在当前用户的启动项中，不需要管理员。
- **零依赖**：单个 C# 文件，用 Windows 自带的 .NET Framework 编译器即可构建。

### 系统要求

- Windows 10 2004（版本 19041）或更高版本、Windows 11（需要 `pnputil /enable-device`）
- .NET Framework 4.7.2 或更高（Windows 10 1803 起已内置）

### 构建

```powershell
.\build.ps1
```

生成 `TouchToggle.exe`。也可以直接执行：

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /codepage:65001 /optimize+ /win32manifest:app.manifest /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /out:TouchToggle.exe TouchToggle.cs
```

### 使用

1. 双击 `TouchToggle.exe`，任务栏右下角出现图标。
2. **左键**点击图标打开面板；**右键**有“打开面板 / 退出”。
3. 第一次使用时，面板顶部会提示安装**后台助手**。点击“立即安装”并在 UAC 中确认。程序会被复制到 `C:\Program Files\TouchToggle\` 并从那里继续运行。
4. 之后直接点击开关即可，不会再弹出管理员提示。
5. 在“设置”中可以开启**开机自启动**，或**卸载**后台助手。

> ⚠️ 关闭笔记本自带的触摸板后，只能用触摸屏或外接鼠标操作，请注意。

### 工作原理

更改设备启用状态是 Windows 规定的管理员操作，所以分成两部分：

| 部分 | 权限 | 作用 |
|---|---|---|
| 托盘程序 | 普通用户 | 显示面板，通过 WMI 读取设备状态 |
| 后台助手（计划任务 `TouchToggle-Helper`） | SYSTEM | 调用 `pnputil /enable-device`、`/disable-device` |

1. 托盘程序把请求写到 `%ProgramData%\TouchToggle\queue\*.req`，然后运行 `schtasks /run /tn TouchToggle-Helper`。
2. 计划任务以 SYSTEM 身份启动 `C:\Program Files\TouchToggle\TouchToggle.exe --helper`，读取请求并执行开关。
3. 托盘程序轮询设备状态，确认生效后删除请求文件。

**安全措施**

- 后台助手只接受当前枚举到的 HID 数字化仪设备（触摸屏 / 手写笔 / 触摸板），其他设备一律忽略。
- 被提权运行的程序位于只有管理员可写的 `Program Files` 中。
- 后台助手以 SYSTEM 身份运行，不继承用户的环境变量，例如 `COR_PROFILER`。
- 计划任务只授予当前用户“读取 + 运行”权限，不能修改。
- 请求目录由安装程序重新创建，所有者是管理员，只有 SYSTEM、管理员和安装时的用户能访问。
- 后台助手对请求目录只读：不写、不删、跳过符号链接，并忽略两分钟前的旧请求。
- 调用系统程序时一律使用 `System32` 下的完整路径。

### 命令行参数

| 参数 | 说明 |
|---|---|
| （无） | 启动托盘程序 |
| `--install <域\用户>` | 安装后台助手（需管理员，面板会自动调用） |
| `--uninstall` | 卸载后台助手（需管理员） |
| `--helper` | 后台助手模式，由计划任务调用 |
| `--snapshot <out.png> [dark\|light] [缩放]` | 把面板渲染成图片，用于截图或调试 |

### 卸载

1. 在面板“设置”中点击后台助手旁的**卸载**，确认 UAC。会删除计划任务和 `C:\Program Files\TouchToggle\`。
2. 如果开启过开机自启动，可以先在面板中关闭它；或者删除注册表值 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\TouchToggle`。

---

## English

A small Windows tray utility that lets you **turn individual touchscreens, pens and touchpads on or off**. It's especially handy on dual-screen laptops (e.g. ASUS ZenBook Duo), where you may want touch disabled on just one screen.

### Features

- **Per-device switches**: touchscreens, pens and touchpads are grouped, each with its own switch, plus "turn all on / off" for groups with more than one device.
- **Knows which screen is which**: uses the calibration data from Windows "Tablet PC Settings" to label each touch device as the **primary** or **secondary** screen, with its resolution.
- **Polished flyout**: modeled on Windows Quick Settings. It follows the system dark/light mode and accent color, has animated switches and supports high DPI.
- **Status at a glance**: the tray icon is green when all touchscreens are on, orange when some are, and red with a slash when all are off.
- **Admin only once**: the app runs as a normal user. You approve UAC once at install time, and toggling devices never prompts again.
- **Start with Windows**: one switch. It uses the per-user Run key, so no admin is needed.
- **No dependencies**: a single C# file, built with the .NET Framework compiler that ships with Windows.

### Requirements

- Windows 10 2004 (build 19041) or later, or Windows 11 (for `pnputil /enable-device`)
- .NET Framework 4.7.2 or later (built in since Windows 10 1803)

### Build

```powershell
.\build.ps1
```

This produces `TouchToggle.exe`. Or run the compiler directly:

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /codepage:65001 /optimize+ /win32manifest:app.manifest /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /out:TouchToggle.exe TouchToggle.cs
```

### Usage

1. Run `TouchToggle.exe`. An icon appears in the notification area.
2. **Left-click** the icon to open the panel. **Right-click** gives "Open panel / Exit".
3. On first use, the panel asks you to install the **background helper**. Click the install button and approve UAC. The app copies itself to `C:\Program Files\TouchToggle\` and keeps running from there.
4. From then on, just flip the switches. There are no more admin prompts.
5. Under Settings you can turn on **start with Windows** or **uninstall** the helper.

> ⚠️ If you disable the laptop's built-in touchpad, you'll need the touchscreen or an external mouse to get around.

The UI is currently in Chinese.

### How it works

Changing a device's enabled state requires admin rights in Windows, so the app is split into two parts:

| Part | Privilege | Role |
|---|---|---|
| Tray app | Standard user | Shows the panel and reads device state via WMI |
| Helper (scheduled task `TouchToggle-Helper`) | SYSTEM | Runs `pnputil /enable-device` and `/disable-device` |

1. The tray app writes a request to `%ProgramData%\TouchToggle\queue\*.req`, then runs `schtasks /run /tn TouchToggle-Helper`.
2. The task starts `C:\Program Files\TouchToggle\TouchToggle.exe --helper` as SYSTEM. The helper reads each request and applies it.
3. The tray app polls device state until the change takes effect, then deletes its request.

**Safeguards**

- The helper only acts on currently enumerated HID digitizer devices (touchscreen / pen / touchpad). Any other device ID is ignored.
- The elevated binary lives in `Program Files`, which only admins can write to.
- The helper runs as SYSTEM, so it doesn't inherit the user's environment variables (e.g. `COR_PROFILER`).
- The scheduled task grants the current user read + run rights only, not modify.
- The installer recreates the queue folder with Administrators as owner. Only SYSTEM, Administrators and the installing user can access it.
- The helper treats the queue as read-only: it never writes or deletes there, skips symlinks, and ignores requests older than two minutes.
- System tools are always launched by their full `System32` path.

### Command-line options

| Option | Description |
|---|---|
| (none) | Start the tray app |
| `--install <DOMAIN\user>` | Install the helper (admin; the panel calls this for you) |
| `--uninstall` | Remove the helper (admin) |
| `--helper` | Helper mode, invoked by the scheduled task |
| `--snapshot <out.png> [dark\|light] [scale]` | Render the panel to an image, for screenshots or debugging |

### Uninstall

1. In the panel, click **Uninstall** next to the helper under Settings and approve UAC. This removes the scheduled task and `C:\Program Files\TouchToggle\`.
2. If you turned on start with Windows, switch it off in the panel first, or delete the registry value `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\TouchToggle`.

---

## License

[MIT](LICENSE)
