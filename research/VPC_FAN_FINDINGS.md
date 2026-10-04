# VPC 旧实验与 EC 固件证据更正

日期：2026-09-30；21CX / HYCN42WW。**VPC 风扇查询和设置实验已停用。旧回读不能证明风扇模式被设置。**

## 保留的原始事实

驱动 AcpiVpc.sys 的 selector 14 / IOCTL 0x831020C4 发送 VPC 命令 0x2B；selector 6 / IOCTL 0x831020C0 发送 0x22，先将输入数据写入 VDAT。驱动队列通过 ACPI VPCR/VPCW 操作 VCMD/VDAT。驱动哈希及原始反汇编见 vpc-transport-disassembly.json。

旧实验写入 1 后回读 1、写入 0 后回读 0，DYTC 状态不变。原始记录保留在 vpc-verification.json 的 Experiment 中。其中 Passed、TargetVerified、OriginalStatesRestored 是旧检查器基于原始字节相等给出的结果，不能当作风扇控制通过或物理状态恢复的证据。

## 新静态证据

官方包内的 HYEC42WW 候选 EC 程序中，偏移 0x104C5 的处理例程读取 XDATA 0x83B0；ACPI DSDT 将 VCMD/VDAT 定义为 ERAM 的 B0/B1。固件在 0x104CF 调用 0x7DA8 的表分派助手。

主代理按助手指令核对表格式：每条为目标地址高字节、低字节、selector；00 00 后的两字节为默认目标。0x104D2 起的表包含 10/11/12/13/1A/1B/1C/2E/2F/31，没有 22 或 2B。默认目标是 858B；对应候选映射中的 0x1058B 字节为 `E4 90 83 B0 F0 22`：清零 A、加载 DPTR=83B0、写入、返回，没有更新 83B1。

这提供了与旧实验一致的替代解释：未知命令被清除，VDAT 保留先前写入的值，后续“查询”得到原样数据。具体代码银行映射及本机运行时 EC 镜像尚未完整核对，因此不宣称已证明所有固件路径都不支持风扇控制；但旧证据已不足以支持该接口。

证据见 [ec-command-tables.json](ec-command-tables.json)、[ec-command-context.txt](ec-command-context.txt) 及 [EC_FIRMWARE_FINDINGS.md](EC_FIRMWARE_FINDINGS.md)。

## 程序处理

- LenovoVpcReader 不再打开设备或提交命令，返回 unavailable、空 RawMode 和停用原因。
- 两份 VPC 查询/设置脚本在设备访问前拒绝执行；Test 的 PlanOnly 仅返回 Retired，不提出设置动作。
- 界面明确显示接口停用，不再把回读字节标成风扇模式。
- DYTC 的独立验证不受此更正影响。有效 RPM、任意调速及曲线目标仍未完成。
