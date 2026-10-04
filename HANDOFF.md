# ThinkBookControl 开发交接

更新日期：2026-10-04。面向接手 UI 迭代、验证与后续开发的人。

发布保留规则：按用户要求，仅保留最近三版 ThinkBookControl 成品包，当前为 `ThinkBookControl-style-v2`、`ThinkBookControl-ui`、`ThinkBookControl`。`ThinkBookControl-preview`、`ThinkBookControl-control` 和 `ThinkBookControl-modern-standby` 旧成品目录已永久清理；其历史诊断记录仍用于追溯。`Ec*Probe` 目录属于诊断工具，不计入软件的三个发行版本。GitHub Releases 同样按发布时间保留最近三版；当前首次公开发行标记为 `v0.1.0`，ZIP 包含完整运行依赖与许可证，排除个人配置。首次 GitHub 发行为已验收美化版的打包，不重新触发硬件控制验收。

最新视觉美化版：`artifacts/publish/ThinkBookControl-style-v2/ThinkBookControl.exe`。本次只调整 `MainWindow.xaml`、新增 `Styles/VisualTheme.xaml`、曲线绘制外观，以及两个自建对话框共享样式资源；未修改控制策略、后端、配置和生命周期逻辑。默认 960×760、最小 800×640，仍使用蓝/橙区分风扇 1/2，主要操作固定在底部。

本版验证：`artifacts/diagnostics/ui-style-v2/verification.log` 离线 UI 回归通过；同目录 `control-policy.log` 为 27 项通过，`publish.log` 为 Release 自包含发布成功。十张截图涵盖默认/最小尺寸 100%/150%/200% 离线渲染、手动页、行校验错误、预设命名及未保存修改对话框；实体屏幕 DPI 切换与本机硬件回归本次未重测，实际睡眠验收仍延期。上一版 UI 与原始包均保留。

美化版包 SHA256：桌面 `452C0B2F450CA4E41EDFFAD2D0867A18B7BDCDA36EE9A2225921D670987EA09E`；后端 `818CECC01CB38633719C92FCEF8702ADC66F78FB08D27675F539D45224D6EC88`。本次重新构建包含初始化后的 Git 版本元数据，后端源码没有改动；下面的旧包哈希与报告仍指对应历史包。

## 1. 接手时的状态

2026-10-03 旧版基准包已交付可在本机正常 Windows 11 启动模式下实际调速的 WPF 软件。双路手动控制、真实 CPU 温度曲线、异常恢复、关窗退出和实际 Modern Standby 睡眠唤醒均有旧版实测记录。测试签名已关闭；HVCI（内存完整性）和驱动阻止列表保持开启。继续 UI 开发无需重新开启测试签名或切换开发驱动。

本轮 UI 改造围绕本机自用、图形双路曲线和命名预设，采用中文浅色 Windows 工具样式。`ThinkBookControl-ui` 已完成 Release 自包含发布；离线 UI 检查、六张渲染截图视觉检查和 27 项控制策略检查已通过。本机正常启动、真实控制与新版窗口/托盘验收均通过，报告见第 6 节；用户明确本轮先交付，实际睡眠验收延期。旧版睡眠实测结果不代替新版睡眠验收。

当前只确认支持 **ThinkBook 14 G4+ IAP / 机器类型 21CX / BIOS HYCN42WW / Intel i5-12500H**，即用户的中国发售 2022 年机型。其他型号、BIOS 和干净系统的驱动安装流程尚未验收。

- 工作区：`C:\dev\myfancontrol`
- 旧版已验收包：`artifacts/publish/ThinkBookControl/ThinkBookControl.exe`，保留用于对照
- 本轮 UI 发布包：`artifacts/publish/ThinkBookControl-ui/ThinkBookControl.exe`
- 本轮离线证据：`artifacts/diagnostics/ui-redesign-offline` 中的日志与六张截图
- 本轮本机总报告：[report.json](artifacts/diagnostics/ui-redesign-regression-20261004-050337-438/report.json)
- 使用说明：[PRODUCT_README.md](PRODUCT_README.md)
- 路线及历史：[DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md)、[research/PROGRESS.md](research/PROGRESS.md)
- 旧版正常启动证据：[report.json](artifacts/diagnostics/product-normal-boot-20261003-042806-435/report.json) 与同目录 `control-product.json`

`DEVELOPMENT_PLAN.md` 和研究日志保留了早期阶段边界、失败与等待记录。旧版控制基线看上述报告；本轮 UI 功能与验收状态看本文件，不混用两版结论。

## 2. 现有产品功能

| 功能 | 当前行为 |
| --- | --- |
| 界面 | 默认 960×760、最小 800×640，中文浅色；实时摘要与操作状态保持可见，设备/完整传感器/错误折叠 |
| 监控 | CPU/GPU 温度、双路 EC 实际 RPM、当前模式与已应用目标；缺失、失败显式显示 |
| 手动 | 两路独立目标，1500–7500 RPM，100 RPM 步长 |
| 曲线 | 同图叠加两路，选择一路拖拽或编辑图右侧同步节点表格；节点增删、双向整条复制 |
| 预设 | 自建命名双路预设，保存/另存为/重命名/删除；默认曲线只读 |
| 草稿与应用 | 编辑、选择预设、保存不改变正在运行的配置；点击应用才接管，未保存与尚未应用独立显示 |
| 温度来源 | CPU 有效温度的最大值，排除 Distance to TjMax；GPU 目前只展示 |
| 曲线计算 | 线性插值，2 °C 降温迟滞、1 秒最短写入间隔、降速限制 300 RPM/秒，输出量化到 100 RPM |
| 高温 | CPU 达到 90 °C 时请求两路 7500 RPM |
| 温度失效 | 缺失、超过 5 秒或来自未来的样本触发恢复固件控制 |
| 配置 | EXE 同目录 `control-settings.json` 保存两路输入、曲线及命名预设；兼容旧四字段文件，草稿不自动保存 |
| 托盘 | X/最小化隐藏且继续控制并保留草稿；首次非模态提示，双击显示 |
| 真正退出 | 托盘“恢复固件并退出”，有未保存修改时可保存/放弃/取消；等待正常恢复与后端释放，失败如实报告 |
| 睡眠/系统退出 | 挂起恢复固件，唤醒不自动接管；注销/关机不弹草稿对话框、不取消系统退出，保留后端父进程退出保护 |
| 启动 | 载入保存参数但仍由固件控制；没有开机启动或自动接管 |

曲线至少两个节点，温度严格递增、RPM 不递减，RPM 在 1500–7500 之间且为 100 的倍数，最高温节点的 RPM 必须为 7500，温度不超过 90 °C。图形编辑与表格校验复用产品曲线/策略约束。图是静态映射预览，运行时迟滞、写入间隔和降速限制仍由原策略处理。

配置 JSON 保留 `Fan1Rpm`、`Fan2Rpm`、`Fan1Curve`、`Fan2Curve`，新增可选 `CurvePresets` 列表；每项含 `Name`、`Fan1Curve`、`Fan2Curve`。缺少新字段的旧文件仍能载入，默认曲线由程序提供。保存的是参数与自建预设，不是控制模式；保存、应用成功和未保存修改处理会写入相应参数，普通编辑不自动写盘。保存失败独立显示，不能被控制状态覆盖。诊断调用共用控制入口，可能更新该文件；发布新包前确认保存参数符合预期。

## 3. 架构与改动入口

```text
WPF MainWindow
  ├─ SensorMonitor → CPU/GPU 温度 + 只读 EC RPM
  ├─ FanControl.Core → 曲线与控制决策（纯计算）
  └─ ControlClient → stdin/stdout JSON → 独立 ControlHost
                                      └─ 固定 RPM 写入 → 正式 PawnIO + 签名 LpcIO
```

| 文件/目录 | 责任及接手提示 |
| --- | --- |
| `src/ThinkBookControl/MainWindow.xaml` | 浅色布局、实时摘要、曲线/手动页、折叠详情与固定操作区 |
| `MainWindow.xaml.cs` | 监控循环、摘要与详情展示、真正关闭后的资源释放 |
| `MainWindow.Editor.cs`、`Editing/` | 双路节点草稿、校验、命名预设与未保存修改处理 |
| `Controls/CurveEditor.cs` | 同图两路映射绘制、一路选择与节点拖拽，不访问硬件 |
| `MainWindow.Control.cs` | 手动/曲线/恢复入口、模式状态、配置、保护和应用成功历史 |
| `MainWindow.Tray.cs` | NotifyIcon、菜单、显示/隐藏和图标释放 |
| `MainWindow.Lifecycle.cs`、`App.xaml.cs` | X 隐藏、显式退出、会话结束的非阻塞清理；区分隐藏和真正结束 |
| `MainWindow.Power.cs` | 原生睡眠通知订阅、消息去重、注销 |
| `ControlClient.cs` | 独立后端的进程与 JSON 通道管理 |
| `Monitoring/TemperatureSnapshot.cs` | 温度、转速及错误的数据模型，适合作为 UI 展示输入 |
| `Monitoring/SensorMonitor.cs` | LibreHardwareMonitor 采样与 EC 反馈读取；不设置风扇 |
| `src/FanControl.Core/FanControlPolicy.cs` | 曲线、迟滞、时序、传感器失效决策，适合离线单独测试 |
| `src/EcPmcConfigProbe/ControlHost.cs` | 后端所有权、身份门禁、命令处理、心跳/父进程恢复 |
| `FixedRpmOverrideWriter.cs` | 固定硬件字段写入与选择器恢复 |
| `FanOverrideBaselineReader.cs` | 双快照、身份核验及零覆盖读取 |
| `src/ThinkBookControl/Diagnostics/` | 产品共用流程的实际控制、关窗、睡眠验收 |

当前使用 code-behind 与 partial class，没有完整 MVVM 分层。保持异步控制入口：`ApplyManualAsync`、`ApplyCurvesAsync`、`RestoreFirmwareAsync`、`ProcessControlSnapshotAsync`、`StopControlAsync`。显式退出统一调用 `RequestExitAsync`；`BeginSessionEnding` 处理无对话框、无等待的系统退出清理，`ShowFromTray` 供托盘和诊断恢复窗口。控制操作由 `_controlGate` 串行化，后台采样通过 Dispatcher 更新 WPF。新编辑器、图表和托盘不直接写硬件。

控件名称和私有展示方法被 `tests/UiVerification` 使用。更换控件或绑定后同步调整该验证程序，避免旧检查失去意义。

## 4. 后端接口与需要保留的行为

产品使用本机已有 **PawnIO 2.2.0 正式签名驱动 + 固定签名 LpcIO 模块**。曾研究 PMC2 `68/6C`，但最终产品通过已实测的固定 SRAM 字段控制，日常运行不需要自定义端口模块或开发驱动。

- 仅设置 `080C` / `080D`：两路目标 RPM，以 100 RPM 为单位；`0` 解除 RPM 覆盖。产品不写 `080E` / `080F` PWM 覆盖。
- EC 身份为 `5571`、映射 `1060=00`，机型/BIOS、管理员身份及模块固定哈希均检查。
- `Global\ThinkBookControl.21CX.RpmOwner` 保证单个控制所有者；底层访问使用全局 ISA 互斥。
- 初始化要求一致且零覆盖的基线。仅“双快照变化、身份/选择器恢复正常、两份覆盖均零”允许最多八次只读重试；其他失败立即停止。
- 只有命令成功确认后才更新 `AppliedControl`，不能把拟请求值当成已成功应用值。
- 后端独立监视父进程 PID 与启动时间、stdin EOF、UTC 心跳间隔。超过 10 秒无心跳或父进程退出会清除两路覆盖。
- UI 关闭 stdin 后等待后端清理，不能用强杀后端替代恢复。
- `ReadAckAsync` 等待确认 8 秒，释放等待后端退出 15 秒。这些托管等待超时不等于能打断卡住的内核 I/O。

后端启动参数：`--control-host <parentPid>`。先返回启动确认，之后每行一个 JSON 命令，每条对应 `{ "ok": true/false, "error": null/字符串 }`。

```json
{"mode":"manual","fan1":3600,"fan2":3800}
{"mode":"firmware"}
{"mode":"heartbeat"}
```

UI 开发应通过 `ControlClient` 使用接口；不需要手工启动后端做布局测试。

本机是 S0 Modern Standby。曾出现 `SystemEvents.PowerModeChanged` 没有通知的问题，已改用 `RegisterSuspendResumeNotification(HWND)` 与 `WM_POWERBROADCAST`。保留隐藏期间的订阅以及真正关闭时的注销，不能只换回传统事件。本轮 X 已改为隐藏；需验证隐藏期间采样/心跳继续、托盘显式退出恢复以及实际睡眠恢复。恢复结果未确认时不能宣称已经恢复。

## 5. 构建、运行与发布

技术栈：C#、.NET 10、WPF，Windows Forms 仅用于托盘，LibreHardwareMonitorLib 0.9.6。项目自带本机 SDK `.tools/dotnet/dotnet.exe`（10.0.401）和包缓存。

在工作区 PowerShell：

```powershell
Set-Location C:\dev\myfancontrol
$env:DOTNET_CLI_HOME = Join-Path $PWD '.tools/cli-home'
$env:NUGET_PACKAGES = Join-Path $PWD '.tools/nuget-packages'
& .tools/dotnet/dotnet.exe build src/ThinkBookControl/ThinkBookControl.csproj -c Release --no-restore
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Publish-Product.ps1 -DirectoryName ThinkBookControl-ui
```

本轮固定发布到 `ThinkBookControl-ui` 新目录，保留 `ThinkBookControl` 旧版已验收包用于对照。发布脚本同时生成桌面和 `control-host`，复制 README 与许可证，打印 DLL 哈希；`win-x64` 自包含，无需另装 .NET 运行时。真实运行 EXE 的 manifest 要求管理员，会弹 UAC。

复制整个发布目录，不要只复制 EXE。不要覆盖正在运行的软件目录。新机器接手时 `.tools` 缓存不一定齐全，应准备 .NET 10 SDK 并按项目恢复依赖；构建还依赖保留的上游 `LpcIO.bin` 和 `artifacts/pawn-modules/*.bin` 嵌入资源。不要只移交桌面源码子目录。

当前包依赖目标电脑已安装的 PawnIO；干净机器的安装器、驱动分发和卸载尚未交付。保留 `licenses` 中的通知，依赖来源记录见 `research/sources.json`。本轮构建/发布出现 NU1900（NuGet 漏洞查询源离线）与 WFO0003（Windows Forms 分析器针对 DPI manifest 的提示）；WPF 主窗口保留 PerMonitorV2 manifest，Release 构建与自包含发布已完成，日志见 `artifacts/diagnostics/ui-redesign-offline/publish.log`。

本轮 UI 发布包 SHA256：

```text
ThinkBookControl.dll  AF0ACE266F0C218714A99915A7D15BA30103910440B73FF2461E01D7C469F706
EcPmcConfigProbe.dll  2BEAE3840CFA5782C08ED6D78DF88C60002BD049ADE5D74CED1C444E5D063B60
```

后端哈希与旧版基准一致；桌面 UI DLL 为本轮新版本。

## 6. 验证方式与已有证据

UI/纯逻辑开发先用离线检查，无需管理员或硬件写入：

```powershell
& .tools/dotnet/dotnet.exe run --project tests/ControlPolicyVerification/ControlPolicyVerification.csproj -c Release --no-restore
& .tools/dotnet/dotnet.exe run --project tests/UiVerification/UiVerification.csproj -c Release --no-restore -- artifacts/diagnostics/monitor-after-restart-20261002-171300.json artifacts/diagnostics/ui-redesign-offline
```

UI 检查读取保存的采样数据，验证新摘要、编辑/预设与草稿状态、默认/最小尺寸、键盘用例、托盘和原生电源消息路由。本轮 Release 运行退出码 0，日志为 `artifacts/diagnostics/ui-redesign-offline/verification.log`；该程序没有启动传感器监控或控制后端。纯策略 27 项检查通过，见同目录 `control-policy.log`。

默认 960×760 与最小 800×640 的六张 `ui-default/minimum-{100,150,200}.png` 已视觉检查，主要操作页脚固定，右侧节点表格列宽为 80/90/138。100/150/200% 图像来自 96/144/192 DPI 的 RenderTargetBitmap 离线渲染，不能称为实体屏幕 DPI 切换验收。电源消息也是合成输入，实际睡眠仍待后续人工验收。

改动控制策略时检查纯计算用例；改动硬件后端时再运行其无硬件自检：

```powershell
& artifacts/publish/ThinkBookControl-ui/control-host/EcPmcConfigProbe.exe --self-check
```

本轮正常启动/控制验收可显式选择新包。在目标机管理员 PowerShell 运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-ProductNormalBoot.ps1 -DirectoryName ThinkBookControl-ui
```

它验证非测试启动、HVCI、阻止列表和原驱动，再实际执行两组手动目标、CPU 温度曲线、温度失效、EOF、心跳超时和父进程退出恢复。`-DirectoryName` 默认仍为旧 `ThinkBookControl` 目录以兼容旧调用；本轮须明确指定 `ThinkBookControl-ui`，报告的 `ProductDirectory` 记录目标包。本轮报告 `artifacts/diagnostics/product-normal-boot-20261004-050337-871/report.json` 的 NormalBootVerified / ProductControlVerified 均 true、Error=null、CI=00003401，同目录 `control-product.json` 的 Succeeded=true、Errors=[]。

窗口验收也直接指定新包。确认旧版与新版都已从托盘真正退出，再运行：

```powershell
$taskUiExe = Join-Path $PWD 'artifacts/publish/ThinkBookControl-ui/ThinkBookControl.exe'
$taskWindowJson = Join-Path $PWD ('artifacts/diagnostics/ui-window-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.json')
& $taskUiExe --window-test --output $taskWindowJson
```

`--window-test` 通过产品共用入口应用真实手动目标，检查 X 隐藏后控制与心跳继续至少 12 秒、托盘恢复显示，以及 `RequestExitAsync` 真正退出后覆盖清零。报告含 `BeforeHide`、`AfterHide`、`AfterExit` 与对应 Evidence 阶段，兼容保留 `BeforeClose`、`AfterClose` 别名。该测试会实际控制硬件。本轮 `artifacts/diagnostics/ui-redesign-regression-20261004-050337-438/window-product.json` 的 Succeeded / CloseHidden / ShownFromTray / ExplicitlyExited 均 true、Errors=[]；BeforeHide 与 AfterHide 的 C/D 都为 36/38，隐藏 12 秒后反馈为 3575/3763 RPM，AfterExit 的 C/D/E/F 都为 0。

实际睡眠使用同一新包及独立的新 JSON 路径：

```powershell
$taskSleepJson = Join-Path $PWD ('artifacts/diagnostics/ui-sleep-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.json')
& $taskUiExe --sleep-test --output $taskSleepJson
```

本轮按用户决定先交付，实际睡眠验收延期，以上命令留给下一次接手。等待诊断就绪后，由人执行 Windows 睡眠约 20 秒并唤醒；测试不主动触发睡眠。X 进入托盘不会终止观察，托盘“恢复固件并退出”才提前终止；唤醒后应保持固件控制。旧版睡眠通过记录不能代替这一新版测试。

`Test-ProductFinalChecks.ps1` 是 2026-10-03 基准测试签名恢复的专用流程，会串联启动设置恢复脚本；普通 UI 回归无需重复该恢复流程。

本轮验收记录：

| 项目 | 状态与证据 |
| --- | --- |
| Release 构建与自包含发布 | 已完成，`ui-redesign-offline/publish.log`，新包哈希见第 5 节 |
| 纯控制策略 | 27 项通过，`ui-redesign-offline/control-policy.log` |
| 离线 UI、编辑、配置、键盘、托盘与合成电源消息 | Release 运行退出码 0，`ui-redesign-offline/verification.log` |
| 默认/最小窗口与六张缩放图像 | 已视觉检查，`ui-default/minimum-{100,150,200}.png`；属于离线渲染，未实测实体屏幕 DPI 切换 |
| 新包正常启动与产品控制 | 已通过，`product-normal-boot-20261004-050337-871/report.json` 与同目录 `control-product.json`，目标目录 `ThinkBookControl-ui` |
| 新包窗口/托盘持续控制与显式退出恢复 | 已通过，`ui-redesign-regression-20261004-050337-438/window-product.json`，隐藏 12 秒仍保持目标，退出后覆盖为零 |
| 新包真实 Modern Standby 睡眠唤醒 | 用户决定延期，后续由人触发并补 JSON |

本机总报告为 `artifacts/diagnostics/ui-redesign-regression-20261004-050337-438/report.json`，Succeeded / ControlVerified / WindowVerified 均 true，Error=null。测试后没有残留产品或后端进程，诊断写入的测试配置已清理，旧版发布包哈希保持不变。

以下证据均属于 2026-10-03 旧版基准包：

| 已有证据 | 结果 |
| --- | --- |
| `product-normal-boot-20261003-042806-435` | 正常启动与产品控制均 true，Error=null，CI=00003401 |
| 同目录 `control-product.json` | Succeeded=true，Errors=[]；3600/3800→4200/4400 目标，反馈约3540/3743→4130/4329；曲线及四类恢复通过 |
| `product-final-20261003-041049-046` | 实际关窗、S0 睡眠唤醒和启动设置恢复通过；唤醒后 C/D/E/F 均0 |
| `pawnio-boot-restore-20261003-041543-376` | 原驱动核验、testsigning 元素删除、HVCI/阻止列表1 |

转速实测依据是 EC 测速反馈，没有独立测速仪校准。目标 RPM 与反馈有正常偏差，不应要求完全相等。验收不要假设固件初始转速一定低于某个固定目标；用不同目标之间的反馈响应来验证。

旧版已验收包 SHA256（不能用于识别本轮 UI 包）：

```text
ThinkBookControl.dll  5A0F665DBED1CFF3062BEDE17682A7FFBB43187D1EC8591857D746528C75FCDD
EcPmcConfigProbe.dll  2BEAE3840CFA5782C08ED6D78DF88C60002BD049ADE5D74CED1C444E5D063B60
```

## 7. 本轮边界与后续工作

本轮已确认的实现是实时摘要、图形与表格双路曲线、自建命名预设、草稿/保存/应用分离，以及 X 入托盘和显式退出。保持现有 WPF 与控制后端，不增加历史图表、日常日志导出、预设导入/导出、主题切换、托盘偏好、开机启动、自动接管、服务化或安装器。

这些附加功能和其他机型支持需另行确认，不能据历史建议认定为本轮交付项。用户决定本轮先交付，后续首要工作是新版实际睡眠唤醒验收。

UI 阶段不必重复 PMC 协议探索、开发驱动切换或 BIOS 固件分析。若要扩展温度来源、控制范围、睡眠后自动接管或硬件型号，再单独设计并实测对应行为。

历史约束：用户明确要求不提交 GitHub Issue、不联系维护者。现有驱动研究与固件资料保留在 `research/`，供需要时查阅。

## 8. 给接手者的起始任务

接手首项是本轮延期的真实睡眠验收：使用 `ThinkBookControl-ui` 的 `--sleep-test`，待诊断就绪后人工睡眠约 20 秒并唤醒，核验零覆盖、固件控制、窗口/托盘可用和未自动接管，补充新版 JSON 与结果。先阅读编辑器、控制、托盘、生命周期和电源 partial 文件，保持当前草稿/保存/应用语义及系统退出处理。离线 UI、策略、发布、真实控制与新版窗口/托盘证据已经记录，无新代码改动时无需重复构建或整轮控制验收；保留旧版基准包、本轮发布包和报告。
