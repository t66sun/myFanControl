# 21CX / HYCN42WW 固件接口研究进度

日期：2026-09-30。以下结论来自本机只读导出的 ACPI 表、CIM 元数据和本机 Lenovo 服务二进制字符串，已验证 MMC 散热模式切换及恢复，尚未验证任意转速写入。

## 导出与核对

- `scripts/Export-AcpiTables.ps1` 使用 Windows 固件读取 API，验证表签名、长度及 ACPI 校验和。
- 本机 DSDT 未出现在 API 枚举列表中，但显式请求 DSDT 可以取得 410506 字节的数据。
- Windows API 同名表读取有局限；ACPICA `acpidump -b -n SSDT` 补齐了 20 张 SSDT。
- `scripts/Expand-AcpiTables.ps1` 对 DSDT 和所有 SSDT 校验签名、长度、校验和、唯一 SHA256 与数量，并分别反汇编。
- 证据位于 `artifacts/acpi/expanded-manifest.json`、各 `.dsl` 和 `.log` 文件。部分表定义重叠，联合反汇编出现 AE_ALREADY_EXISTS；单独反汇编完成，但外部方法引用仍需逐项核对。
- 只导出 DSDT、SSDT、FACP，没有导出可能包含 Windows 产品密钥的 MSDM。

## 已确认的路径

### Lenovo WMI Super Resolution

DSDT 中 `\\_SB.WMIS.WMSR` 的方法 ID 1 返回 `SLSR`；ID 2/3 设置 `LESR`；ID 4 返回 2。结合本机 LENOVO_SR_DATA 元数据的 Super Resolution 描述，它不是可设风扇转速的接口。

### EC 与散热模式

- EC 设备：`\\_SB.PC00.LPCB.H_EC`，PNP0C09。
- `_CRS` 描述 0x62/0x66 端口，但 `ERAM` 的字段使用 SystemMemory 地址 `0xFE0B0300`，长度 0xFF；不能仅凭端口声明就照搬传统 NBFC 的 8 位 EC 配置。
- `ITSM` 为 3 位模式字段；多条 DYTC 分支通过 ECWT/ECCC 改变该字段或发送模式命令。
- `RDER` 使用 CMDB/DAT0/DAT1/NUMB 等邮箱字段实现 16 位地址读取，并有状态检查与超时。这是另一条需研究的路径，尚未调用。
- `VPC0` 的 `_HID` 是 VPC2004；VPCR/VPCW 读写 VCMD/VDAT，`DYTC` 是散热/性能模式的候选接口。
- `DYTC` 存在 FBC、APM、AQM、MMC 等逻辑，但固件里出现名称不等于本机运行时支持该功能，也不证明支持任意 RPM。

### 不能视作调速证据的字段

- ssdt13 的 FAN0～FAN4 是 ACPI 逻辑散热设备。FNCL 将状态写入 VFN0～VFN4，尚未建立它们与实际风扇转速的关系；不代表机器有五个物理风扇。
- ssdt10 的 OSD1/OSD2 包含 CPU Fan Duty Cycle、RPM 描述，但 OSDD 返回空包，RPMD 返回新建的空缓冲区；这些文字不足以证明有效转速遥测。
- DSDT 的 CFAN 仅发现外部声明；尚未找到当前导出表中的实际字段定义。

## 本机驱动信息

诊断脚本已运行：识别 21CX / ThinkBook 14 G4+ IAP，采集 40 个相关 root/wmi 类，Errors 为零。LENOVO_OTHER_METHOD、LENOVO_FAN_TEST_DATA、LENOVO_GAMEZONE_DATA 仍未发现。

已安装并运行的 PawnIO 可作为温度监测依赖。ACPIVPC 驱动存在，Lenovo Notebook ITS 服务 LITSSVC 的程序为 LNBITSSvc.exe。对该程序只读字符串检查发现 EnergyDrv、DYTC、Cooling Boost、IFC 和模式切换日志；下一步应核对对应 IOCTL 及输入输出布局，先验证无副作用的能力/状态查询。

## 尚未解决

1. 有效风扇 RPM 遥测来源。
2. 任意转速/档位设定协议、范围及自动控制恢复语义。
3. MMC 模式往返之外的控制能力，以及 OEM 服务与长期控制的协调。
4. MMIO EC 与端口 EC 的关系，及邮箱读事务的同步机制。

以上未解决前，不根据其它机型地址试写风扇控制值。

## 参考

- [Microsoft GetSystemFirmwareTable 文档](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-getsystemfirmwaretable)
- [Microsoft EnumSystemFirmwareTables 文档](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-enumsystemfirmwaretables)
- [ACPICA 官方发布](https://github.com/open-acpica/acpica/releases)，本次工具版本 20260408，来源记录在 `acpica-source.json`。

## 已验证的 Windows 传输与模式往返

主代理对已安装 AcpiVpc.sys 进行只读反汇编，确认 IOCTL 0x8310213C 的 DWORD 参数进入 RVA 0x1260 的 DYTC 包装函数。函数构造 ACPI integer-input，调用 0x32C004 并解析 integer-output。CreateFile 打开 `\\.\EnergyDrv`，请求 access=0；无需管理员即可执行已审查的查询和本次 MMC 往返。

| DYTC 命令 | 用途 | 本机响应 |
| --- | --- | --- |
| 0 | 协议查询 | 0x50000101 |
| 2 | 当前状态 | 0x08012B01（初始及恢复后） |
| 3 | 功能能力 | 0x19F10001 |
| 6 | MMC 模式能力 | 0x000C0001 |
| 7 | MMC 模式 | 0x00020001（性能） |

SET 使用命令 1、function=0xB、valid=1：性能 0x0012B001，安静 0x0013B001。本机 DSDT 显式处理模式 2/3：性能发送 ECCC(0x60,0xA0/A6) 并写 ITSM=4；安静发送 ECCC(0x60,0xA4) 并写 ITSM=2，随后还有 ECCC(0x60,0xA5)。这些操作由固件完成，应用不直接写 EC。

Test-LenovoThermalModes.ps1 的实测记录确认目标模式和恢复状态保持 3 秒且回读一致。该验证不含物理 RPM 遥测。FBC bit2 未声明，不能因为 DSDT 含 SET FBC 分支而启用全速功能。

新增只读索引脚本 Audit-LenovoDriver.py，生成 artifacts/diagnostics/lenovo-driver-index.json，记录本机驱动 SHA256、IOCTL 常量和 ACPI 方法名候选。它不加载驱动、不发命令；常量候选仍需核对实际控制流。进一步审查确认 IOCTL 0x83102140 分支调用 RVA 0x1BF4 的 ITHC 包装函数；所有导出 DSL 中未找到 ITHC 定义，未尝试调用。MHIF/MHPF/MHCF 的本机方法处理电池固件、SMBus 和固件更新相关字段，没有 RPM 证据。

证据：artifacts/diagnostics/lenovo-thermal-state.json、thermal-mode-roundtrip.json、lenovo-driver-index.json 及本机 artifacts/acpi/DSDT.dsl。RPM 接口与曲线控制仍是后续核心工作。