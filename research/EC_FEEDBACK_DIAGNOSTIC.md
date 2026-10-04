# IT5571 固定 SRAM 反馈诊断

状态：管理员实机读取已经完成。20 轮基线读取与 60 轮模式关联读取均通过身份、映射和选择器恢复检查；工具本身不提供调速功能。详细结果见下方“实机结果”。

## 新增映射线索

本机一次管理员身份实测已返回 0x5571、1060=0x00，见 [身份诊断](EC_IDENTITY_DIAGNOSTIC.md)。官方 HYEC42WW 候选代码将两条计数转换结果写入 XDATA 8800–8803，见 [反馈例程](EC_FAN_FEEDBACK.md)。

另一台 ASUS Q410 的 IT5571 研究作者报告：通过 4E/4F 的 10/11/12 间接选择器可以读取 8000–97FF 的工作 RAM，其程序没有设置 1060 bit7。原始研究：[TECHNICAL.md](https://github.com/hz12opensource/zenbook-q410-powerfix/blob/ff0870853cfdd52dd858727d609f2d8a07a46a24/docs/TECHNICAL.md)。这里只将其用作读取候选路径的依据；其主机写入限制、ASUS 内存布局及电源修改不能视为本机能力。

另克隆 System76 开源 EC 固件，提交 8faf1c2c16e54dd1ea0b605de7bd5684f5e77931。其 pnp.c 为该固件配置 SMFI/H2RAM，说明接口配置受固件影响；其命令和风扇配置不是联想固件协议。没有运行这两个仓库的代码、更新或安装工具。

## 固定范围

| 项目 | 限制 |
| --- | --- |
| 机型 / BIOS | 21CX / HYCN42WW |
| 芯片 / 原映射 | 每轮必须 5571 / 1060=00；轮末再次核对 1060 |
| 传输 | 已安装 PawnIO、固定哈希 LpcIO、端口 4E/4F |
| 数据地址 | 0800–080F 与 8800–880F，各重复读取两次 |
| 0800 的含义 | 已观察到与固件反馈格式一致的两路数据；不推导其他地址的一般别名规则 |
| 8800 的含义 | 内部 XDATA 地址；本机主机侧该窗口重复读不一致，不用作产品转速来源 |
| 采样 | 默认 20 轮；--sample-feedback-long 固定 60 轮；每轮完成后间隔 500ms，睡眠/暂停可能使墙钟跨度延长 |
| 写入 | 仅间接地址选择器；数据选择器 12 不写入；不修改 1060、风扇目标、PWM 或固件模式 |
| 恢复 | 每轮 finally 恢复 10/11/2E 并回读；失败停止后续采样 |
| 互斥 | 每轮持有 ISA 全局互斥锁，轮间释放；锁忙/遗弃时停止 |

保持原映射状态进行读取，避免套用另一 ThinkBook 项目先设置 bit7 再读取 Cxxx 的初始化路径。若读数全零、全 FF、不同窗口不一致或数值变化，只保留事实，不调整映射位试错。

## 输出与成功含义

每轮输出两段窗口的两份原始十六进制字节、开始/结束时间、重复字节是否相等、芯片和映射读值、选择器恢复及错误。

只有两次完整字节相等时才生成 Word01BigEndian / Word23BigEndian；它们是候选 16 位整数，不标为 RPM。字节相等也不证明读取原子性、窗口别名或物理单位。零原样保留，不当作已经确认风扇停转。

ReadCompleted 与退出码 0 只表示全部受限读取及选择器恢复通过。PhysicalRpmVerified 和 ControlVerified 始终为 false，须由后续实机证据独立验证。退出码 2 表示权限/机型/芯片/映射/读取/恢复失败；3 为输出无法创建（设备访问前）；64 为参数错误。现有输出不覆盖，不接受任意地址参数。

代码：[Program.cs](../src/EcFeedbackProbe/Program.cs)、[FeedbackReader.cs](../src/EcFeedbackProbe/FeedbackReader.cs)。工具通过编译链接复用原身份工具的端口客户端，不改变其源码和已验证发布文件。

## 主代理检查

- 首次编译修正模拟数据的 byte 类型；之后构建零编译警告/错误。自包含发布完成，NuGet 漏洞元数据访问出现 NU1900，未声称漏洞审查通过。
- 发布二进制执行模拟检查通过：固定地址集、芯片不符停止、初始/末尾映射变化、变化字节不解码、零值原样保留及 846 个操作前/后故障用例。
- 实际普通权限采样返回 2：管理员身份 false、DeviceOpenAttempted=false、PawnClientInitialized=false、样本为零，未访问驱动或端口。
- 现有输出碰撞返回 3，原文件哈希保持一致；任意地址参数返回 64。
- 主代理核对最终发布 DLL 与构建 DLL 的哈希一致。检查摘要：[ec-feedback-probe-checks.json](ec-feedback-probe-checks.json)。模拟检查不证明硬件协议或 RPM。

## 执行方式

不访问硬件：

```powershell
& .\scripts\Build-EcFeedbackProbe.ps1
& .\artifacts\publish\EcFeedbackProbe\EcFeedbackProbe.exe --plan
& .\scripts\Start-EcFeedbackDiagnostic.ps1 -PlanOnly
```

下列命令已在用户后续“继续推进”授权下执行一次，固定输出已存在，启动脚本会拒绝重复运行：

```powershell
& .\scripts\Start-EcFeedbackDiagnostic.ps1
```

固定输出 `artifacts/diagnostics/ec-feedback-admin.json`，等待原进程结束。启动脚本不重试；已有该输出时拒绝再运行。此前单次身份诊断与本次读取分别记录；本次读取依据用户后续授权执行。


## 管理员会话中的启动

启动脚本先检查当前 PowerShell 的实际管理员令牌：已有管理员权限时直接启动并等待子进程；否则才使用 Verb=RunAs 请求 UAC。PlanOnly 会报告 ExecutionIsAdministrator 与 RequiresUac，不启动采样。应用以管理员身份打开和 Full Access 都不能代替实际令牌检查，也不会自动扩大此前一次诊断授权。实际普通权限启动器经 RunAs 成功启动管理员诊断；管理员关联采样工作进程的子工具继承了管理员令牌，无额外 UAC。当前根工具进程仍为普通权限。

## 实机结果（2026-10-01 核对）

- 20 轮基线：0800 窗口重复读取 20/20 一致，两路反馈值为 4303/4329，目标字节均为 43；8800 窗口 20/20 不一致。全部芯片 5571、1060=00，选择器恢复成功，无错误。
- 60 轮关联：性能模式切至安静后，0804/0805 的目标从 34 降至 31，两路反馈逐渐降低；这是反馈语义的支持证据，尚未用独立物理仪器校准，也未验证手动调速。
- 安静请求和恢复请求分别发出于 2026-09-30 20:20:05Z 和 20:20:17Z。恢复请求在长时间暂停之前已发出。恢复状态回读因暂停延迟至次日 02:42Z，仍与原始性能模式一致；另一次 14:09Z 查询仍为性能模式。
- 长采样存在三处超过两秒的轮间断点，分为四段，不能描述为连续 30 秒采样；睡眠之后的零反馈不单独证明物理停转。
- 同一管理员工作进程中的三轮 CPU 绝对温度均有效，约 49–69°C；这是当时的测量，不代表当前温度。
- 所有工具已结束，退出码为 0；未写手动风扇目标、PWM 或映射配置。没有把回读成功当作任意调速或异常恢复验收。

原始结果：`artifacts/diagnostics/ec-feedback-admin.json`、`ec-feedback-mode-correlation.json`、`thermal-mode-correlation.json`、`temperature-admin-correlation.json`。主代理独立解析与哈希记录：[基线核对](ec-feedback-baseline-verification.json)、[关联核对](ec-feedback-correlation-verification.json)。
