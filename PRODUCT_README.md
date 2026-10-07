# myFanControl

2026-10-07 `v0.2.0`：新增 CPU/GPU 数字温度托盘图标、每扇风扇独立的温度滞回和升降速延时，以及曲线实时目标标记。本版仅本地交付。自包含包共用一套 .NET 10 桌面运行时，轻量包使用系统的 .NET 10 Desktop Runtime x64。

适用设备：ThinkBook 14 G4+ IAP / 21CX，BIOS HYCN42WW，Windows 11。v0.2.0 已通过 57 项合成控制策略检查和离线 UI、配置、托盘、模拟电源通知检查。本轮未重新进行真实硬件或实际睡眠验收；下方本机报告均属于对应历史版本。

每个版本交付自包含与轻量两个 ZIP，同一 `SHA256SUMS.txt` 包含两包的校验值。

## 使用

下载见 [Releases](https://github.com/t66sun/myFanControl/releases)。每个版本提供两个 Windows x64 ZIP：默认 ZIP 自带 .NET，可直接运行；文件名含 `framework-dependent` 的轻量 ZIP 不带 .NET，需要系统已有对应版本的 Microsoft Windows Desktop Runtime x64。完整解压后运行其中的 `myFanControl.exe`。发行 ZIP 不包含个人 `control-settings.json`；升级时可把旧版该文件复制到新版 EXE 同目录。

v0.2.0 的自包含 ZIP、轻量 ZIP、安装向导和统一校验文件位于 `release/v0.2.0/`。向导不安装 PawnIO 驱动，保留个人 `control-settings.json`；升级前请在旧版托盘选择“恢复固件并退出”。安装程序未签名。本版未上传到 GitHub Release，GitHub 最新版仍为 v0.1.3。

保留整个发布目录，运行 `myFanControl.exe`，确认管理员提示。启动和睡眠唤醒后均由固件控制，需要手动点击应用；没有开机启动或自动接管。窗口打开或隐藏时，CPU/GPU 数字图标均保持显示；悬停显示一位小数，双击打开窗口，两者共享托盘菜单。图标可能位于 Windows 的隐藏图标区，可在系统设置中调整。读数失效显示 `--`。

界面采用中文浅色 Windows 工具样式，默认 960×760，最小 800×640。顶部显示 CPU/GPU 温度、两路实际 RPM、已应用目标和当前模式；主要操作页脚固定，设备信息、完整传感器和诊断错误在折叠详情中。

### 主题色

点击右上角“主题色”，在 Windows 颜色选择器中选择颜色并确认，即时生效且自动保存。点击“重置”恢复默认色。浅色主题自动使用深色文字；曲线和焦点保持可读。主题保存在 `control-settings.json` 的可选 `ThemeColor` 字段（`#RRGGBB`），旧配置无需修改。保存失败时保留原色并显示错误；切换主题不会保存编辑草稿或触发风扇控制。

### 自动曲线与预设

1. 在“自动曲线”页选择预设，或从“默认曲线”开始。每个命名预设同时保存两路曲线；默认曲线为只读，可将修改另存为自己的预设。
2. 同一张图叠加蓝色风扇 1 与橙色风扇 2 曲线。选择一路后，拖拽该路节点或在图右侧表格修改温度和 RPM；可增加、删除节点，也可使用“风扇 1 → 2”或“风扇 2 → 1”复制整条曲线。
3. 曲线至少两个节点，温度严格递增，RPM 不递减。RPM 范围 1500–7500，步长 100；最高温节点必须为 7500 RPM，温度不超过 90°C。无效节点会提示原因，修正后才能保存或应用。
4. “保存”“另存为”“重命名”“删除”管理命名预设。编辑、选择预设和保存均不会改变正在运行的控制；点击“应用自动曲线”后才启用当前双路配置。

曲线图是温度到目标 RPM 的静态映射预览。运行控制仍使用有效 CPU 温度最大值，排除 Distance to TjMax；GPU 只用于监测。实际目标还受降温迟滞和降速限制影响，实际 EC 测速反馈可能与目标略有偏差。

选择风扇 1 或 2，分别编辑升温滞回、降温滞回、升速延时、降速延时，均为 0～10 的整数（温度单位 °C，时间单位秒）。默认值依次为 0、2、0、0。滞回以该风扇上次成功改变转速时的温度为基准；例如基准 70°C、升温滞回 3°C、升速延时 2 秒，则温度持续达到 73°C 两秒后按最新温度升速。条件中断或方向反转会重新计时，成功调速后重新建立基准。首次应用、高温保护和温度失效恢复不等待；仍保留每秒最多降速 300 RPM 的限制。按约一秒样本判断，实际响应会受采样周期影响。

圆圈表示启用曲线在当前温度下的目标，方块表示已应用目标，顶部实际转速仍来自 EC 测速。编辑草稿与启用曲线不同，会显示启用曲线的点线和草稿说明。参数配置保存为 `Fan1Response`、`Fan2Response`（`HeatingHysteresis`、`CoolingHysteresis`、`HeatingDelaySeconds`、`CoolingDelaySeconds`）。旧 `IncreaseDelaySeconds` 自动迁移；新参数优先。曲线预设和曲线复制仍只包含节点，响应参数独立保存。编辑或保存不改变正在运行的控制，点击“应用自动曲线”后生效。

### 手动控制与状态

切换到“手动控制”，分别输入两路 1500–7500 RPM、100 的倍数，点击“应用手动转速”。编辑输入不会即时改变转速。手动和自动曲线均保留温度保护：CPU 达到 90°C 时请求两路 7500 RPM；有效 CPU 温度缺失、超过 5 秒或来自未来时恢复固件控制。

“未保存修改”表示当前输入还没有写入配置；“尚未应用”表示当前输入尚未用于控制。保存参数与应用参数是两个动作，请以顶部当前模式、已应用目标和控制状态判断正在运行的配置。切换预设或真正退出时，如果存在未保存修改，可选择保存、放弃或取消。

### 托盘与退出

点击 X 或最小化都会进入托盘，保留草稿并继续监控及已启用的控制；第一次会显示非模态提示。双击托盘图标或选择“显示窗口”恢复窗口。“恢复固件控制”解除两路覆盖；“恢复固件并退出”才真正退出，等待正常后端释放。如恢复未能确认，程序会明确报告错误。

系统挂起时恢复固件控制，唤醒后不自动重新启用。系统注销或关机不会弹出草稿对话框，也不会被软件取消；独立后端仍保留父进程退出、输入通道关闭和心跳超时的恢复保护。

## 配置

EXE 同目录的 `control-settings.json` 保留 `Fan1Rpm`、`Fan2Rpm`、`Fan1Curve`、`Fan2Curve` 四个字段，并新增 `CurvePresets`，其中每项包含 `Name`、`Fan1Curve`、`Fan2Curve`。旧版只有四个字段的文件仍可读取。默认曲线由程序提供，自建预设保存到列表。

草稿不会自动保存。保存预设、处理未保存修改或成功应用参数时才写入相应配置；保存失败会单独提示。下次启动载入保存的输入和预设，仍由固件控制。

本机后端使用现有 PawnIO 2.2 正式签名驱动与签名 LpcIO 模块，无需安装开发驱动。保留根目录的 `myFanControl.exe`、`EcPmcConfigProbe.exe`、共享运行时文件、`zh-Hans`/`zh-Hant` 资源和 `licenses` 子目录。两个 EXE 仍各自运行为独立进程，控制后端由桌面程序启动；精简版不再使用旧的 `control-host` 子目录。

## 验收记录

v0.1.3：主题色即时更新、序列化/重载、默认重置、白/黄/黑配色、无效颜色和保存失败检查通过。确认未完成的手动输入与当前控制模式保持原状。11 张截图及日志位于 `artifacts/diagnostics/ui-theme-20261006`，包括紫色主题示例。

v0.1.2：现有离线 UI 回归通过，覆盖默认 960×760、最小 800×640 与 100%/150%/200% 离线渲染、表格错误、节点操作、双路复制、预设操作、草稿提示、键盘操作、托盘及模拟电源通知。截图与日志位于 `artifacts/diagnostics/ui-refresh-20261006`。默认窗口增加曲线、节点表格及增删按钮无遮挡检查。以下均为历史报告。

精简版本轮后端自检、27 项策略与离线 UI 检查通过，十张默认/最小尺寸、手动页、校验与对话框截图位于 `artifacts/diagnostics/runtime-compact/ui`。这些缩放图仍是离线渲染，不代表实体屏幕 DPI 切换。

本轮真实验收总报告为 `artifacts/diagnostics/runtime-compact-regression-20261004-093634-605/report.json`，Succeeded / ControlVerified / WindowVerified 均 true、Error=null。正常启动报告 `artifacts/diagnostics/product-normal-boot-20261004-093635-043/report.json` 的 NormalBootVerified / ProductControlVerified 均 true，目标包为 `myFanControl-compact`；窗口报告为总报告同目录 `window-product.json`，隐藏 12 秒后目标 3600/3800 RPM 仍生效，真正退出后 C/D/E/F 全零。实际睡眠验收仍延期。

以下是历史 `myFanControl-ui` 的证据，离线记录位于 `artifacts/diagnostics/ui-redesign-offline`：

- 离线 WPF 界面、编辑、配置、托盘及合成电源消息检查通过，Release 运行退出码 0，见 `verification.log`；未启动传感器监控或控制后端。
- 默认/最小窗口的六张 `ui-default/minimum-{100,150,200}.png` 已完成视觉检查。这些是 96/144/192 DPI 的离线 RenderTargetBitmap 渲染，不代表在实体屏幕切换 DPI 的实测。
- 27 项纯控制策略检查通过，见 `control-policy.log`；Release 自包含发布完成，见 `publish.log`。
- 本机正常启动与真实控制通过：`artifacts/diagnostics/product-normal-boot-20261004-050337-871/report.json`，目标包为 `myFanControl-ui`，NormalBootVerified / ProductControlVerified 均 true，Error=null；同目录 `control-product.json` 为 Succeeded=true、Errors=[]。
- 历史 UI 窗口/托盘通过：`artifacts/diagnostics/ui-redesign-regression-20261004-050337-438/window-product.json`。X 隐藏 12 秒后两路目标仍为 3600/3800 RPM，EC 反馈 3575/3763 RPM；托盘恢复显示与真正退出均通过，退出后 C/D/E/F 覆盖为零。
- 总报告 `artifacts/diagnostics/ui-redesign-regression-20261004-050337-438/report.json` 的 Succeeded / ControlVerified / WindowVerified 均 true，Error=null。用户决定本轮先交付，真实 Modern Standby 睡眠唤醒验收延期，作为后续首项工作。

历史视觉美化版的离线回归、十张截图、27 项策略与发布记录位于 `artifacts/diagnostics/ui-style-v2`。v0.2.0 本地发布包位于 `artifacts/publish/myFanControl-v0-2-0` 与 `myFanControl-v0-2-0-framework-dependent`，本版证据位于 `artifacts/diagnostics/v0.2.0-final`。旧版睡眠记录不能代替本版实测；历史硬件说明见 [HANDOFF.md](HANDOFF.md)。

2026-10-03 旧版基准包已有以下记录：

- 正常启动：`artifacts/diagnostics/product-normal-boot-20261003-042806-435/report.json`，NormalBootVerified / ProductControlVerified 均 true，Error=null；测试签名关闭，HVCI 和驱动阻止列表开启，原 PawnIO 2.2.0 路径与哈希核验通过。
- 两组手动目标 3600/3800 → 4200/4400 RPM，EC 反馈约 3540/3743 → 4130/4329 RPM；真实 CPU 温度曲线、温度缺失、输入通道关闭、心跳超时和父进程退出恢复通过。
- 旧版实际关窗退出与 Modern Standby 睡眠唤醒：`artifacts/diagnostics/product-final-20261003-041049-046`，两项通过，恢复后两路 RPM 与 PWM 覆盖均为零。新版 X 已改为隐藏，需按新版退出路径重新验收。

开发、发布和新版实机验收命令见 [HANDOFF.md](HANDOFF.md)；历史实测见 [research/PROGRESS.md](research/PROGRESS.md)。
