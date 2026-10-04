# ThinkBookControl

面向 ThinkBook 14 G4+ IAP（21CX）的 Windows 风扇控制软件开发项目。

**当前版本是只读温度与 EC 风扇监控预览版。已接入两路 EC 报告转速；手动调速与自动曲线执行仍在开发。新版应用的三轮管理员采样已通过：CPU 温度与两路 EC 反馈有效，身份/映射/选择器恢复一致。**

## 运行预览版

打包输出：`artifacts/publish/ThinkBookControl-preview/ThinkBookControl.exe`。自包含版本不要求全局安装 .NET。

双击打开中文温度与风扇监控窗口。CPU 温度与 EC 风扇读取需要右键“以管理员身份运行”，Windows 可能显示 UAC。程序不会自行提权；普通权限下会提示读数不可用。EC 读取仅适用于 21CX / HYCN42WW，风扇 1/2 暂未对应到 CPU/GPU。零值仅表示 EC 报告的零反馈，不能单独证明物理停转。GPU 显示本机实际可用的传感器。

## 只读诊断

```powershell
& .\scripts\Get-ThinkBookDiagnostics.ps1
& .\scripts\Get-PawnIoAccess.ps1
& .\scripts\Expand-AcpiTables.ps1
& .\artifacts\publish\ThinkBookControl-preview\ThinkBookControl.exe --probe --samples 5 --output .\artifacts\diagnostics\temperature-probe.json
```

诊断 JSON 包括逐轮温度、风扇读数、ecFanTelemetry 原始双读字节与身份/映射/恢复检查、设备标识、错误和启动时固件模式查询。界面显示查询取得的模式；CPU 温度与 RPM 可用性分别报告。退出码：0 表示每轮都有 CPU 绝对温度读数；2 表示 CPU 温度未持续取得；3 表示输出文件写入失败；64 表示参数错误。完整的采样和清理错误仍应查看 JSON，退出码不代表控制硬件已验证。

硬件接口诊断需要足够的 Windows 查询权限。ACPI 导出需要 `.tools/acpica` 下的官方 ACPICA 工具；导出脚本不执行 ACPI 控制方法、不导出 MSDM 产品密钥表。所有诊断默认写入 `artifacts`。

## 构建

需要 .NET 10 SDK，当前工作区已有 `.tools/dotnet` 的本地 SDK。

```powershell
& .\scripts\Build.ps1
& .\scripts\Build.ps1 -Publish
```

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