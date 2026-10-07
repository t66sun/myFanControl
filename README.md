# myFanControl

面向 ThinkBook 14 G4+ IAP（21CX）的 Windows 风扇控制软件开发项目。

当前版本为 `v0.2.1`：新增常驻 CPU/GPU 数字温度托盘图标，两把风扇分别设置升降温滞回、升降速延时，曲线显示启用目标与已应用目标。保留自定义主题色、命名预设、草稿状态和硬件保护。[版本说明](docs/releases/v0.2.0.md)。v0.2.1 安装向导新增自动准备官方 PawnIO 驱动。

## 下载与运行

下载见 [GitHub Releases](https://github.com/t66sun/myFanControl/releases)。每个版本固定提供两个 Windows x64 ZIP：默认包自带 .NET，可直接运行；文件名含 `framework-dependent` 的轻量包不带 .NET，需要先安装对应版本的 Microsoft Windows Desktop Runtime x64。完整解压后运行 `myFanControl.exe`，按 Windows 提示确认管理员权限。根目录同时包含 `myFanControl.exe` 与 `EcPmcConfigProbe.exe`，`licenses` 随包保留。便携 ZIP 要求本机已安装 PawnIO 2.2 或更高正式签名驱动；安装向导可在缺少或较旧时联网下载安装。

v0.2.1 的两个 ZIP、安装向导和校验文件位于 `release/v0.2.1/`。向导提供中英文页面、快捷方式及卸载入口，并在需要时下载官方 PawnIO 2.2.0，验证固定 SHA256 与 Authenticode 签名后安装。首次缺驱动安装需要联网，按提示重启后再启动应用。升级前先在旧版托盘选择“恢复固件并退出”。构建命令：`& scripts/Build-ReleaseInstaller.ps1 -Version 0.2.1 -ReleaseDirectory release/v0.2.1`（Inno Setup 7，默认 `.tools/innosetup/ISCC.exe`）。构建前将官方 2.2.0 安装文件放在 `.tools/pawnio-2.2.0/PawnIO_setup.exe`；元数据见 `scripts/PawnIoDependency.json`，此文件仅供构建校验，不随包分发。[版本说明](docs/releases/v0.2.1.md)。

当前仅验证 ThinkBook 14 G4+ IAP / 21CX / BIOS HYCN42WW。两路曲线使用有效 CPU 温度最大值；GPU 只展示，风扇保持“1／2”命名。编辑和保存不会改变控制，点击“应用”才启用。X 或最小化隐藏到托盘，右键托盘选择“恢复固件并退出”真正退出。使用与配置说明见 [PRODUCT_README.md](PRODUCT_README.md)，验证证据和源码构建依赖见 [HANDOFF.md](HANDOFF.md)。

v0.2.0 发布目录为 `artifacts/publish/myFanControl-v0-2-0-final` 与 `myFanControl-v0-2-0-final-framework-dependent`。发行 ZIP 不带个人配置，升级时可复制旧版 `control-settings.json`；旧版升速延时会迁移为两把风扇的独立参数。

v0.2.0 的 57 项合成控制策略检查及离线 UI 检查通过；配置、托盘、实时曲线和多倍缩放截图位于 `artifacts/diagnostics/v0.2.0-final`。此前 v0.1.3 主题回归保留在 `artifacts/diagnostics/ui-theme-20261006`。真实硬件与实际睡眠本轮未重测。

## 只读诊断

```powershell
& .\scripts\Get-ThinkBookDiagnostics.ps1
& .\scripts\Get-PawnIoAccess.ps1
& .\scripts\Expand-AcpiTables.ps1
& .\artifacts\publish\myFanControl-v0-1-3\myFanControl.exe --probe --samples 5 --output .\artifacts\diagnostics\temperature-probe.json
```

诊断 JSON 包括逐轮温度、风扇读数、ecFanTelemetry 原始双读字节与身份/映射/恢复检查、设备标识、错误和启动时固件模式查询。界面显示查询取得的模式；CPU 温度与 RPM 可用性分别报告。退出码：0 表示每轮都有 CPU 绝对温度读数；2 表示 CPU 温度未持续取得；3 表示输出文件写入失败；64 表示参数错误。完整的采样和清理错误仍应查看 JSON，退出码不代表控制硬件已验证。

硬件接口诊断需要足够的 Windows 查询权限。ACPI 导出需要 `.tools/acpica` 下的官方 ACPICA 工具；导出脚本不执行 ACPI 控制方法、不导出 MSDM 产品密钥表。所有诊断默认写入 `artifacts`。

## 构建

需要 .NET 10 SDK，当前工作区已有 `.tools/dotnet` 的本地 SDK。

```powershell
& .\scripts\Build.ps1
& .\scripts\Publish-Product.ps1 -DirectoryName myFanControl-new
& .\scripts\Publish-Product.ps1 -DirectoryName myFanControl-new-fd -FrameworkDependent
& .\scripts\Package-Release.ps1 -Version '<新版本>' -SelfContainedDirectory myFanControl-new -FrameworkDependentDirectory myFanControl-new-fd
```

发布脚本要求目标目录尚不存在，并自动恢复依赖。默认生成自包含包，`-FrameworkDependent` 生成不含 .NET 的轻量包。两个变体都将桌面与后端合并到同一根目录，只复用哈希相同的文件，遇到不同哈希冲突会停止。打包脚本同时生成两个 ZIP 和含两行校验值的 `SHA256SUMS.txt`；这是后续版本的固定交付标准。历史 `Build.ps1 -Publish` 仅生成桌面预览包。

开发依赖固定为 LibreHardwareMonitorLib 0.9.6，仅启用 CPU/GPU；不启用主板、控制器或内存扫描，不调用风扇控制 API，不自动安装驱动。

## 研究与进度

- [开发计划](DEVELOPMENT_PLAN.md)
- [上游源码预研](research/README.zh-CN.md)
- [本机 ACPI 研究记录](research/ACPI_FINDINGS.md)
- [检查与进度记录](research/PROGRESS.md)
- [转速接口与温度权限核对](research/RPM_INVESTIGATION.md)
- [VPC 旧实验与固件证据更正](research/VPC_FAN_FINDINGS.md)
- [管理员 EC 身份诊断结果](research/EC_IDENTITY_DIAGNOSTIC.md)
- [固定 SRAM 反馈诊断结果](research/EC_FEEDBACK_DIAGNOSTIC.md)
- [EC 转速应用集成与检查](research/EC_TELEMETRY_INTEGRATION.md)
- [候选控制命令研究](research/EC_PMC2_CANDIDATE.md)

LibreHardwareMonitor 使用 MPL-2.0；具体第三方许可证见上游仓库及 NuGet 包。应用另嵌入固定哈希的 PawnIO LpcIO 签名模块，LGPL-2.1-or-later 许可证及源码引用一同打包；不安装或更新驱动。其它上游仓库用于研究参考。

## 已验证的固件模式通道

本机 21CX / HYCN42WW 已实测通过 EnergyDrv / DYTC 完成性能 → 安静 → 性能的往返，恢复后状态回读与初始一致。当前产品支持经验证的两路 RPM 手动目标和自动曲线；适用机型与未完成的新版实机验收见 [HANDOFF.md](HANDOFF.md)。

```powershell
# 只查询，不改变模式
& .\scripts\Get-LenovoThermalState.ps1
# 只打印往返验证计划
& .\scripts\Test-LenovoThermalModes.ps1 -PlanOnly
```

`Test-LenovoThermalModes.ps1` 不带 `-PlanOnly` 会短暂切换性能/安静模式，并在 finally 中恢复原模式；仅允许已验证的型号、BIOS、能力位和初始 MMC 状态。该实验不是任意转速控制，尚未验证应用意外终止时的恢复。

持久研究证据见 [DYTC 验证记录](research/dytc-verification.json) 和 [ACPI 研究记录](research/ACPI_FINDINGS.md)。所有实际运行与结果检查均由主代理完成。
