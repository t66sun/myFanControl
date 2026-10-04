# PMC 配置只读诊断

目标是确定 HYCN42WW 候选 PMC2 命令的实际主机端口，而非执行风扇控制。

固定范围：21CX / HYCN42WW、已有管理员令牌和 PawnIO；固定哈希 LpcIO 模块、4E/4F。原间接身份需为 5571，1060 为 00，选择器恢复成功。然后保存直接配置选择器 07，读取 20/21 芯片 ID，仅匹配 5571 后选择 LDN 11/12，重复读取 30、60–63 的启用及两个基址。finally 恢复 07 并回读，再核对间接身份/映射/恢复。无效或耗时超过两秒的事务不标为成功。

这些寄存器名称和 LDN 来源为 System76 ITE 固件的官方 [pnp.c](https://github.com/system76/ec/blob/8faf1c2c16e54dd1ea0b605de7bd5684f5e77931/src/app/main/pnp.c)。只是读取候选配置，不假定其他固件的端口分配适用于联想。

不调用 BAR 扫描、不进入新的配置模式、不修改启用/基址/映射、不读取 PMC 输出 FIFO、不发送 PMC 命令或手动风扇目标。只写配置/地址选择器并恢复原值；输出原始整数，不把配置读取成功视为控制协议验证。

主代理检查：发布构建通过；模拟正常读取、未知直接芯片、变化配置及 62 个操作前/后故障用例通过，恢复不能假阳性；普通权限运行退出 2，DeviceOpenAttempted=false；已有输出碰撞退出 3 且原哈希不变；不支持的 LDN 参数退出 64。NuGet 漏洞元数据未取得。哈希与结果见 [ec-pmc-config-checks.json](ec-pmc-config-checks.json)。

同一次管理员读取已完成，统一执行会话 88064 已终结，退出码 0。主代理独立核对全部身份/映射/恢复与错误状态，整轮约 20ms。PMC1 启用值 01、两个基址 62/66；PMC2 启用值 01、两个基址 68/6C。原 LDN 为 FE，已恢复并回读。输出路径为 `artifacts/diagnostics/ec-pmc-config-admin-root.json`；已有输出不能覆盖，不重复启动。代码位于 `src/EcPmcConfigProbe/`，独立于已实测的监控预览和原身份工具。

若发现低于 0x100 的 PMC2 端口，现有 LpcIO BAR 允许列表仍不会允许访问；LpcACPIEC 只接受 62/66。后续必须选择符合实际端口及协议的受支持传输，不能为绕过模块限制修改硬件配置。手动调速、自动曲线执行和异常恢复仍未验证。


## 固定状态字节读取扩展

为区分仓库源码限制和当前实际加载的签名二进制能力，新增独立发布目录 `EcPmcConfigProbe-with-status`，保留原发布文件。只有 PMC1/2 配置精确匹配 62/66、68/6C 且前置身份/恢复通过时，才尝试 `ioctl_pio_inb(6C)` 一次；不读取数据 FIFO 68，不调用输出函数，不扫描 BAR。

`--read-configuration-and-status` 输出 Pmc2Status 的 Attempted/ReadCompleted/Raw/Win32Error；原配置 Succeeded 与端口读取可用性分别报告。退出码 0 表示配置与最终身份/恢复有效，不能单凭它认为状态读取或控制成功。驱动拒绝时保留实际 Win32 错误与 null 原字节，不伪装为零。

主代理发布、模拟匹配/不匹配配置及驱动拒绝检查通过，原 62 个故障用例仍通过；普通权限下退出 2，无设备或状态访问。管理员状态扩展的同一次启动请求已返回：Windows 报告“用户取消”，会话 25255 已终结，没有子进程 PID、没有管理员输出、未运行硬件采样。不自动重发；该签名二进制的实际 6C 访问能力仍未知。新检查记录见 [ec-pmc-status-checks.json](ec-pmc-status-checks.json)。

官方分发说明指出官方 PawnIO 只使用其签名模块：[PawnIO](https://pawnio.eu/)。自行编译模块不能假设可加载。本项目没有切换 unrestricted、关闭签名检查、安装新驱动或改变 Windows 启动设置；当前读取扩展使用原有固定哈希签名 LpcIO。


## 固定二进制允许列表补充（2026-10-02）

已离线执行固定 LpcIO.bin 的端口判定：当前初始化组合拒绝 68/6C，且低 BAR 被添加函数拒绝。无需重新弹 UAC 重试已取消探针；下一步转向受支持的受限传输。实机状态读取仍未执行，离线判定与实机结果分开，见 [LPCIO_PINNED_PORT_LIMITS.md](LPCIO_PINNED_PORT_LIMITS.md)。
