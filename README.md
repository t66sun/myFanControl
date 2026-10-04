# ThinkBookControl

面向 ThinkBook 14 G4+ IAP（21CX）的 Windows 风扇控制软件开发项目。

当前版本为 `v0.1.1` 精简版，提供中文浅色界面、实时 CPU/GPU 温度与双路 EC 转速、手动转速控制、图形双路曲线、命名预设及托盘运行。桌面与独立控制后端在同一目录共享一套 .NET 10.0.12 自包含 WindowsDesktop 运行时，并仅保留简体/繁体中文卫星资源。

## 下载与运行

下载见 [GitHub Releases](https://github.com/t66sun/myFanControl/releases)。Windows x64 ZIP 完整解压后运行 `ThinkBookControl.exe`，按 Windows 提示确认管理员权限。本地 `v0.1.1` 包为 `artifacts/publish/ThinkBookControl-compact`；根目录同时包含 `ThinkBookControl.exe` 与 `EcPmcConfigProbe.exe`，运行时和 `licenses` 随包保留，无需另装 .NET。本机使用已安装的 PawnIO 2.2 正式签名驱动，尚未提供驱动安装器。

当前仅验证 ThinkBook 14 G4+ IAP / 21CX / BIOS HYCN42WW。两路曲线使用有效 CPU 温度最大值；GPU 只展示，风扇保持“1／2”命名。编辑和保存不会改变控制，点击“应用”才启用。X 或最小化隐藏到托盘，右键托盘选择“恢复固件并退出”真正退出。使用与配置说明见 [PRODUCT_README.md](PRODUCT_README.md)，验证证据和源码构建依赖见 [HANDOFF.md](HANDOFF.md)。

本地保留最近三版 `artifacts/publish/ThinkBookControl-compact`、`ThinkBookControl-style-v2`、`ThinkBookControl-ui`；原 `ThinkBookControl` 基准成品已清理，历史诊断仍保留。发行 ZIP 不带个人配置，升级时可复制旧版 `control-settings.json`。

精简版后端自检、27 项策略与离线 UI 检查已通过，十张离线截图位于 `artifacts/diagnostics/runtime-compact/ui`。新包正常启动/真实控制与窗口/托盘回归也已通过，报告为 `artifacts/diagnostics/runtime-compact-regression-20261004-062409-704/report.json`；实际睡眠验收仍延期。此前 UI 和美化版报告作为历史证据保留。

## 只读诊断

```powershell
& .\scripts\Get-ThinkBookDiagnostics.ps1
& .\scripts\Get-PawnIoAccess.ps1
& .\scripts\Expand-AcpiTables.ps1
& .\artifacts\publish\ThinkBookControl-compact\ThinkBookControl.exe --probe --samples 5 --output .\artifacts\diagnostics\temperature-probe.json
```

诊断 JSON 包括逐轮温度、风扇读数、ecFanTelemetry 原始双读字节与身份/映射/恢复检查、设备标识、错误和启动时固件模式查询。界面显示查询取得的模式；CPU 温度与 RPM 可用性分别报告。退出码：0 表示每轮都有 CPU 绝对温度读数；2 表示 CPU 温度未持续取得；3 表示输出文件写入失败；64 表示参数错误。完整的采样和清理错误仍应查看 JSON，退出码不代表控制硬件已验证。

硬件接口诊断需要足够的 Windows 查询权限。ACPI 导出需要 `.tools/acpica` 下的官方 ACPICA 工具；导出脚本不执行 ACPI 控制方法、不导出 MSDM 产品密钥表。所有诊断默认写入 `artifacts`。

## 构建

需要 .NET 10 SDK，当前工作区已有 `.tools/dotnet` 的本地 SDK。

```powershell
& .\scripts\Build.ps1
& .\.tools\dotnet\dotnet.exe restore .\src\EcPmcConfigProbe\EcPmcConfigProbe.csproj --configfile .\NuGet.Config -r win-x64 -p:SelfContained=true
& .\.tools\dotnet\dotnet.exe restore .\src\ThinkBookControl\ThinkBookControl.csproj --configfile .\NuGet.Config -r win-x64 -p:SelfContained=true
& .\scripts\Publish-Product.ps1 -DirectoryName ThinkBookControl-new
```

发布脚本要求目标目录尚不存在；示例使用 `ThinkBookControl-new`，若已存在则换未使用的新名字。先恢复两项目的自包含依赖，再发布完整软件。脚本将两套发布输出合并到同一根目录，只复用哈希相同的文件，遇到不同哈希冲突会停止。历史 `Build.ps1 -Publish` 仅生成桌面预览包。

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

本机 21CX / HYCN42WW 已实测通过 EnergyDrv / DYTC 完成性能 → 安静 → 性能的往返，恢复后状态回读与初始一致。桌面预览仍仅查询，任意 RPM/百分比调速和自动曲线尚未验证。

```powershell
# 只查询，不改变模式
& .\scripts\Get-LenovoThermalState.ps1
# 只打印往返验证计划
& .\scripts\Test-LenovoThermalModes.ps1 -PlanOnly
```

`Test-LenovoThermalModes.ps1` 不带 `-PlanOnly` 会短暂切换性能/安静模式，并在 finally 中恢复原模式；仅允许已验证的型号、BIOS、能力位和初始 MMC 状态。该实验不是任意转速控制，尚未验证应用意外终止时的恢复。

持久研究证据见 [DYTC 验证记录](research/dytc-verification.json) 和 [ACPI 研究记录](research/ACPI_FINDINGS.md)。所有实际运行与结果检查均由主代理完成。
