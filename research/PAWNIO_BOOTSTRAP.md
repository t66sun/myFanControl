# PawnIO 开发驱动：启动准备记录

用户已确认 DEVELOPMENT_PLAN.md 中的测试签名路线，并要求继续。首轮只验证加载和查询，HVCI 与驱动阻止列表保持开启，用户手动重启。

## 管理员基线检查

2026-10-02 实际运行 scripts/Prepare-PawnIoDeveloperBoot.ps1（未传 EnableTestSigning），管理员进程退出 0：

- Secure Boot：通过固件 API 确认 false。
- 系统盘 BitLocker：FullyDecrypted，ProtectionStatus Off；未修改加密设置。
- HVCI 与驱动阻止列表注册表 Enabled 均为 1。
- 当前启动项：64290bc4-929d-11f1-9754-f7968b89ab03；当前项没有 testsigning 字段。
- 当前 PawnIO：ROOT\PAWNIO\0000，oem12.inf，版本 2.2.0.0。
- 实际 SYS SHA256：fca6e7d58b0cf38dbb913a2b9e532f48629145d395f454b16a9f58e97b8d3940。
- pnputil /export-driver oem12.inf 已成功导出 SYS/INF/CAT，导出 SYS 哈希与实际安装文件相同。
- bcdedit /export 已成功备份 BCD。

证据目录：artifacts/diagnostics/pawnio-bootstrap-20261002-143256-127。管理员启动会话 9277 已终结，PID 29932 退出 0。

## 启动设置与恢复顺序

启用命令：bcdedit /set {current} testsigning on。先保存原启动项与 BCD 备份，命令执行后读取 /enum 核对。脚本不会安装驱动或重启。

本次原启动项没有 testsigning 字段；恢复应对同一已核对 GUID 使用 bcdedit /deletevalue {64290bc4-929d-11f1-9754-f7968b89ab03} testsigning，并核对字段消失。不要为恢复一个字段直接导入整份 BCD。

先恢复并确认正式版驱动，再移除测试签名，最后由用户手动重启。驱动恢复应使用已导出的准确 2.2.0 包，不能用版本不同的 LHM 安装器替代；具体开发版切换步骤见 PAWNIO_DRIVER_SWITCH_NOTES.md（研究中）。

设置变更后必须重启才会改变当前内核的加载策略。配置命令成功不证明开发驱动或自定义模块能够加载。

## 测试签名配置已写入，等待手动重启

第二次管理员运行传入 EnableTestSigning，实际 PID 13560 退出 0，统一执行会话 60728 已终结。证据目录：artifacts/diagnostics/pawnio-bootstrap-20261002-143938-347。

- 变更前再次导出原驱动与 BCD，原驱动哈希仍匹配。
- 当前内核代码完整性 00003401：HVCI 已开启，运行中的测试签名仍未开启。
- bcdedit /set {current} testsigning on 退出 0；随后 /enum 同一启动 GUID 回读 testsigning Yes。
- 未更改 HVCI、阻止列表、Secure Boot 或 BitLocker；未切换驱动、加载模块、访问 PMC2 或操作风扇。
- 没有发起重启。下一步用户保存工作后手动重启，再核对新内核加载策略。

如果此时取消实验，由于驱动尚未切换，只需恢复原启动项的 testsigning 字段；原驱动无须重装。已写入配置不等于模块加载成功。

## 重启后：开发驱动与恢复实测

当前内核查询 00283603，TESTSIGNING 位 2 和 HVCI 位 400 均已开启。原驱动包和设备实例仍保留。

首次尝试在路径核对处停止：SCM 返回的 ImagePath 带合法 NT 前缀 `\??\`，原脚本未归一化该前缀；随后 sc stop 返回 1052，原驱动未在该次 finally 中恢复。主代理核对错误后用 SeLoadDriverPrivilege 与 NtUnloadDriver 卸载临时传统驱动，返回 00000000，再恢复原路径并启用原设备成功。恢复证据：artifacts/diagnostics/pawnio-dev-restore-20261002-150254-143。

脚本已修复路径比较，并提取已实测的卸载代码到 scripts/PawnIoNativeUnload.cs。恢复中的卸载失败不会再跳过恢复原启动路径。

修复后第二次测试：artifacts/diagnostics/pawnio-dev-load-20261002-150517-653，管理员 PID 21876 退出 1，会话 90662 已终结：

- 原正式版对无效签名模块返回 80090006。
- 禁用原根设备后服务确实进入 Stopped；sc config 与 sc start 均退出 0，开发版服务 Running。
- 手工构造的无硬件 AMX 在开发版返回 8007001F，未证明模块可用，也未进入任何端口访问。
- NtUnloadDriver 返回 00000000，恢复原路径与设备均成功；原 2.2.0 绑定恢复，原模块签名拒绝再次返回 80090006。
- 现阶段实机已证明测试驱动加载与恢复，不再把驱动签名加载作为未经验证的猜测；下一缺口是正确的自定义 AMX。

下一步获取兼容官方 Pawn 编译器，生成可重复构建的无硬件模块后再测试。维持首轮查询范围，不发送控制目标；HVCI 和阻止列表没有关闭。

## 官方编译器模块的结果与对照诊断

会话 75647 已终结，管理员 PID 20428 退出 1。报告 artifacts/diagnostics/pawnio-dev-load-20261002-153049-906 核对的是编译产物 DA2B4ED4FC5405E06FEDD2A08DFEFB533BB81BEF7A13B58E11B5B66CA62DDADC；开发版仍返回 8007001F，NtUnloadDriver 为 0，原驱动路径、绑定和拒绝签名行为已恢复。由此排除了仅手工生成 AMX 的解释，但没有确定内核加载失败的具体位置。

上游 Echo.p 明确提到只有一个 native 时的解释器／编译器问题，并通过 get_arch/debug_print 两个 native 规避。NoHardwareNative.p 按这个现有方法编译，仅查询架构并输出固定内核调试信息，不含端口函数；这是待验证假设，不能声称已解决零 native 模块的问题。

同时增加固定签名 LpcIO 作为已知模块对照。主代理直接核对其固定字节码 main：只调用 native 索引 4（get_arch），比较 ARCH_X64 并返回状态；没有调用配置、PIO 或发现导出。因此本次仅加载 main 不访问硬件端口，也不调用模块其他导出。

下一次实验在原正式版、开发版及恢复后的正式版分别保存签名模块和自定义模块结果。若签名模块在开发版也失败，应定位驱动／模块接口兼容性，而不是继续变更最小 AMX。会话 48931 已单次发起，目前仍等待 Windows UAC 启动，consent PID 13472 存在；未产生新报告，不重发。

该会话后续已终结：Windows Start-Process 明确返回 operation was canceled by the user，退出 1，没有管理员子进程结果或新报告。对照实验没有执行，不自动重新发起。现有原驱动保持恢复后的状态；下一次实机实验需要用户明确要求继续这个被取消的测试。

参考：

- https://learn.microsoft.com/en-us/windows-hardware/drivers/install/the-testsigning-boot-configuration-option
- https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax

## 开发版自定义模块加载成功（2026-10-02）

管理员 PID 22740 实际退出 0。证据：artifacts/diagnostics/pawnio-dev-load-20261002-160821-651/report.json。

- 开发版驱动启动成功。官方签名控制请求在原 2.2.0、开发版和恢复后的 2.2.0 上均返回 `00000000`。
- 自定义无端口 `get_arch`/`debug_print` 模块在开发版加载并正常返回 `00000000`；在原版和恢复后的原版返回 `80090006`（签名拒绝）。这次未加载 PMC2 状态模块。
- `NtUnloadDriver` 返回 `00000000`；原驱动路径、ROOT\PAWNIO\0000 设备绑定及 2.2.0.0 版本恢复成功，`OriginalRestored=true`。
- 该次验证没有尝试硬件 I/O（`HardwareIoAttempted=false`），没有访问 PMC2 端口或设置风扇目标。主程序现已实现固定 6C 状态读取，但尚无证据表明这项代码已在实机运行。RPM 查询、控制和恢复协议仍待后续验证。

## 固定6C读取实测

报告 artifacts/diagnostics/pawnio-dev-load-20261002-162047-424/pmc2-status.json 证明固定状态模块实际读取6C，返回00。机型、EC5571/map00及PMC1/2精确配置门禁通过，前后选择器恢复；原驱动恢复报告OriginalRestored=true。尚未读取68或发送D5查询。

## 首轮查询与原启动设置恢复

一组查询3406/3422 RPM与EC吻合，但三次定时查询整体验证未通过：EC不一致、后续100ms超时，再下一次状态01导致新命令前停止，没有清FIFO。各次原2.2.0驱动均恢复。管理员PID1100已删除新增testsigning元素，读回符合原未设置状态，HVCI/阻止列表1，等待用户手动重启。完整证据与后续工作见PMC2_RUNTIME_RESULTS.md。所有启动的管理员进程及会话均已取得终结状态，没有后台等待的UAC或查询任务。
