# EC 身份诊断：管理员实测通过

工具：[src/EcIdentityProbe](../src/EcIdentityProbe/Program.cs)。独立自包含输出位于 `artifacts/publish/EcIdentityProbe/EcIdentityProbe.exe`，不接入监控程序自动运行。

## 本次请求的范围

仅识别 EC 芯片与地址映射状态。固定为 21CX/HYCN42WW、4E/4F 端口、已存在的 PawnIO 驱动及固定哈希 LpcIO 模块。读取候选地址 2000/2001；只有返回 5570/5571 时才继续读取 1060。没有修改 1060 bit7、没有风扇 RAM 写入、没有 BAR 扫描、没有驱动安装。

“只读”指目标寄存器：通过该间接接口读取时，仍需写入地址选择器。工具保存、恢复并回读选择器 10/11/2E；数据选择器 12 只读，不写载荷。异常时各恢复步骤独立尝试；恢复不一致会报告失败。ISA 全局互斥锁忙或被遗弃时停止。工具自身不请求 UAC。

端口算法参考已克隆的 Thinkbook14PAutoFanController 中 ECRamReadExt_Direct；这是候选协议，尚未确认本机芯片和运行时兼容性。LpcIO 的固定端口/函数 ABI 已由主代理对官方模块源码及 LibreHardwareMonitor 客户端核对；本次实测已确认签名模块被现有驱动接受，身份地址读取与选择器恢复通过；这不证明 SRAM/RPM 地址或控制写入可用。

## 已完成的检查

- 首次 Release 构建：零警告、零错误。
- 自包含发布完成；NuGet 漏洞元数据访问出现 NU1900，因此不声称漏洞审查通过。
- 模拟正常身份、未知芯片停止和 64 个读取/写入前后故障用例通过；禁止写入目标数据，核对恢复成功不能误报。模拟检查不证明实际硬件协议。
- 普通权限运行返回 2、DeviceOpenAttempted=false、PawnClientInitialized=false，结果为空；未访问设备、未提交端口命令。证据在 `artifacts/diagnostics/ec-identity-nonadmin.json`。
- 模块哈希固定为 b3896a1cab0d808fca31fe2ebcae045d59dac690da87b17c858bb8da357eb45e；模块许可证与来源说明随工具保存。

## 运行方式

不访问硬件的计划和模拟检查：

```powershell
& .\artifacts\publish\EcIdentityProbe\EcIdentityProbe.exe --plan
& .\artifacts\publish\EcIdentityProbe\EcIdentityProbe.exe --self-check
& .\scripts\Start-EcIdentityDiagnostic.ps1 -PlanOnly
```

用户允许一次管理员诊断后，运行以下脚本请求 UAC；取消或失败不自动重试：

```powershell
& .\scripts\Start-EcIdentityDiagnostic.ps1
```

输出为 `artifacts/diagnostics/ec-identity-admin.json`。只有已识别芯片、选择器恢复回读成功且无错误时返回 0；这也仅证明身份读取，不能当作 RPM 或调速通过。

## 本次管理员实测（2026-09-30）

用户明确授权“允许一次管理员诊断”后，主代理执行一次启动脚本并等待原进程退出；退出码为 0。采集时间为 2026-09-30T19:37:05.0229192+00:00。

| 项目 | 实际结果 |
| --- | --- |
| Windows 管理员身份 | true |
| PawnIO 打开及固定模块初始化 | 成功 |
| 2000/2001 返回芯片 ID | 0x5571（十进制 21873） |
| 1060 原始值 | 0x00，bit7 未置位 |
| 10/11/2E 选择器恢复及回读 | 通过 |
| 诊断与恢复错误 | 空 |

原始输出：[ec-identity-admin.json](../artifacts/diagnostics/ec-identity-admin.json)。持久摘要及原始结果哈希：[ec-identity-verification.json](ec-identity-verification.json)。主代理独立解析并检查了上述字段。

JSON 的 Plan.HardwareProtocolVerified=false 是执行前计划中的状态；本次结果确认这组身份读取和选择器恢复在本机成功，不能扩大为任意 RAM/控制协议已验证。

没有修改 1060、没有写入风扇目标或 PWM、没有采集实际 RPM。上游另一机型程序设置 1060 bit7 后使用 Cxxx 地址；当前结果显示本机该位为 0，不能直接套用其配置。下一步需确认 IT5571 的 SRAM 地址窗口与本机候选 8800–8803 反馈之间的映射，再进行范围明确的只读遥测。

本次授权已用于上述一次运行；没有重试、追加管理员进程或安装驱动。此前被取消的温度诊断仍未取得结果。
