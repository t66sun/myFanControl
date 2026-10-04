# ThinkBook 14+ 2022 风扇控制预研

研究日期：2026-09-30。当前完成源码检索、克隆、控制链路阅读及本机只读接口检查；尚未验证实际风扇控制。

## 本机信息

从本机注册表及 CIM 查询确认：

| 项目 | 值 |
| --- | --- |
| 厂商 | LENOVO |
| Machine Type | 21CX |
| 型号 | ThinkBook 14 G4+ IAP |
| CPU | Intel Core i5-12500H |
| BIOS | HYCN42WW |
| BIOS 日期 | 2023-01-14 |
| 操作系统 | Windows 11（用户提供） |

未采集序列号。未安装驱动、运行上游程序或写入 EC/BIOS/风扇控制值。

## 已克隆的源码

均为浅克隆，具体提交记录见 `sources.json`。

| 本地目录（相对本文件） | 上游 | 用途与结论 |
| --- | --- | --- |
| `upstream/nbfc` | https://github.com/hirschmann/nbfc | C# 笔记本 EC 控制、Windows 服务、XML 机型配置。当前克隆的 Configs 中未找到 ThinkBook 配置；不能直接认为支持 21CX。 |
| `upstream/LibreHardwareMonitor` | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor | 温度、功耗、转速传感器库；当前源码含 PawnIO 路径。适合优先作为温度监测候选，但能读温度并不意味着能控制本机风扇。 |
| `upstream/TPFanCtrl2` | https://github.com/Shuzhengz/TPFanCtrl2 | C++ ThinkPad 风扇控制参考；默认寄存器是 ThinkPad 布局，不能照搬到 ThinkBook。 |
| `upstream/ThinkBookFanControl-source` | https://github.com/lhzlhz419/ThinkBookFanControl | C# WPF、LibreHardwareMonitor、Lenovo WMI 双风扇控制；README 验证目标为 ThinkBook 16p G6 IAX，本机缺少其依赖的 WMI 类。 |

ThinkBookFanControl 的完整下载长时间未完成，因此改为 partial clone + sparse checkout，保留代码与文档、排除 `csharp/ThinkBookFanControl/lib/` 第三方二进制目录。这份源码用于研究，不是可直接构建的完整依赖包。

常见的 [Rem0o/FanControl.Releases](https://github.com/Rem0o/FanControl.Releases) 明确说明主程序源码闭源，因此没有把发布仓库当作开源控制引擎克隆。

## 源码观察

### NBFC

- `Core/StagWare.FanControl/Fan.cs`：按机型配置的 ReadRegister/WriteRegister 读写 EC，支持 byte/word、百分比与原始值转换、重置值和临界温度保护。
- `TemperatureThresholdManager.cs`：升温和降温使用不同阈值，提供迟滞，避免频繁切换风扇档位。
- `FanControl.cs`：管理 EC 锁、周期更新、初始化寄存器、只读模式和退出重置。
- `Core/Plugins/StagWare.Hardware.LPC/EmbeddedControllerBase.cs`：通过 ACPI EC 的 0x66/0x62 端口进行事务，读命令 0x80、写命令 0x81。这些是传输接口，不是本机风扇寄存器地址。
- 原项目包含旧 OpenHardwareMonitor/WinRing0 驱动路径。适合借鉴结构，Windows 11 驱动实现需另外验证。
- NBFC 的 AutoFanSpeed=101 代表软件按温度曲线自动计算，不等于释放给 BIOS；恢复固件控制取决于机型配置的 reset 语义。
- `EmbeddedControllerBase` 的部分失败路径会返回默认值或在重试耗尽后返回；新实现应明确上报失败，不能把读取失败当成有效的零值。

### ThinkBookFanControl

- `FanController.cs`：通过 `root\\wmi:LENOVO_OTHER_METHOD` 调用 GetFeatureValue/SetFeatureValue。
- 风扇目标 ID：0x04030001、0x04030002；在该项目的硬件路径上，写 0 表示交回固件自动控制。
- 全速开关 ID：0x04020000。风扇范围来自 `LENOVO_FAN_TEST_DATA`。
- `TemperatureReader.cs`：递归更新 LibreHardwareMonitor 传感器，选择 CPU Package/Core Max 等温度来源。
- 上述 ID 和恢复方法只可视为该项目验证硬件的协议，不能推断适用于 21CX。

### TPFanCtrl2 与 LibreHardwareMonitor

- TPFanCtrl2 的 `fancontrol/fanstuff.cpp` 默认使用 0x2F 控制位、0x84 转速字段、0x31 风扇选择位；README 提醒部分 ThinkBook 地址不同。
- [TPFanCtrl2 issue #20](https://github.com/Shuzhengz/TPFanCtrl2/issues/20) 有 ThinkBook 13s G2 的不同寄存器报告，但这不是本机型号，不能直接复用。
- LibreHardwareMonitor 的 `PawnIo/IntelMsr.cs` 是 Intel 温度相关 MSR 访问参考；`PawnIo/LpcACPIEC.cs` 提供 EC 端口访问包装。尚未安装 PawnIO，也未实际加载这些模块。
- NBFC 根许可证为 GPL-3.0，LibreHardwareMonitor 为 MPL-2.0 并有第三方条款，TPFanCtrl2 根许可证为 Unlicense。ThinkBookFanControl 当前检出的文件中未找到独立许可证文件，暂作阅读参考，不直接复制代码。

## 本机接口检查结果

`lenovo-wmi-classes.json` 保存 `root/wmi` 中 LENOVO 前缀的类、方法和属性名；`thermal-wmi-classes.json` 保存补充的散热/ACPI 类名检查结果。两者仅查询元数据，未执行控制方法。

在当前系统/BIOS/驱动环境下，没有发现：

- LENOVO_OTHER_METHOD
- LENOVO_FAN_TEST_DATA
- LENOVO_GAMEZONE_DATA

发现 LENOVO_SR_DATA，方法有 GetDataValue、StartECMonitor、StopECMonitor、GetCapability 等。名称里含 ECMonitor 不代表它是可设定风扇转速的接口；尚未研究其参数和固件实现，也未调用。

这足以排除直接使用上述 ThinkBookFanControl WMI 后端，但不足以证明硬件不支持手动风扇控制；其它 ACPI 接口、厂商驱动或 EC 路径仍需调查。

## 后续实现路线

1. 先制作只读诊断工具：准确识别 21CX/BIOS，列出温度和可用接口，不暴露未验证的调速按钮。
2. 读取并分析本机 ACPI DSDT/SSDT，检查散热方法、EC OperationRegion、风扇遥测与手动/自动模式切换。必要时研究本机 Lenovo 服务/驱动接口；当前尚未导出 ACPI 表。
3. 根据固件分析做有针对性的 EC 只读采样，对照自然温度变化和用户切换 Fn+Q 模式的变化，确认字段含义；避免盲扫写值或使用其它型号地址。
4. 只有在本机控制协议、目标值范围和恢复固件方法均明确后，才实现受限手动控制，再添加迟滞曲线、托盘、日志、温度失效处理、退出及睡眠恢复机制。正常退出清理无法覆盖进程崩溃，需验证固件超时或独立监护机制。

候选技术栈：C# + WPF + LibreHardwareMonitor，控制后端与界面分离。现阶段这是研究方向，尚未确认能实现任意 RPM 控制。
