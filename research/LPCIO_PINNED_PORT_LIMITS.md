# 固定模块的 PMC2 端口限制

2026-10-02，主代理直接解析现有 LpcIO.bin，SHA256 b3896a1cab0d808fca31fe2ebcae045d59dac690da87b17c858bb8da357eb45e。未加载模块、未访问设备、未弹 UAC。此次结论来自二进制，而不是把当前 LpcIO.p 当成该二进制的准确构建来源。

使用官方 PawnIO 的容器/AMX 头定义、PawnPP 的 opcode 定义，解析 512 字节签名包和 64 位、版本 11、flags 0 的 AMX，检查全部 1037 个指令边界及 CASETBL 长度。脚本不执行签名的密码学验证，也不证明任意固件协议。

- ioctl_pio_inb 入口 21D8；2478 CALL 指向 1D18 的端口判定函数。
- 固定初始化选中 4E/4F、BAR 数量为零（客户端构造函数只执行 ioctl_select_slot(1)，不调用 find_bars）。
- 纯内存、有限指令解释器执行实际 ioctl_pio_inb 字节码：62、66、68、6C、CF8、CFF、300 均返回 STATUS_ACCESS_DENIED C0000022，在任何 SYSREQ 前终止。
- 对照 4E、4F、25C、25D 到达模拟 native 边界；解释器在此停止，不执行 native。另验证未初始化时 C00000A3，以及模拟 300 BAR 后 300 可以到达 native。
- 固定二进制 0530 的 BAR 添加函数在 0630 比较值与 100；62/66/68/6C/FF 的同值参数均返回零，且不进入 native，因此扫描 BAR 不能把这些低端口加入允许列表。没有实机扫描。

这证明当前发布的诊断客户端和固定模块组合不能直接读取 PMC2。已取消的管理员状态探针无需为确认这个离线结论重新提权。保留探针和历史取消记录，但不把它作为下一步必须运行的操作。

下一步需要支持 68/6C 的受限传输，并核对 PMC2 与固件控制字段的协议及恢复关系；不能绕过模块签名，也不能用任意其他硬件模块代替。手动 RPM 和曲线执行仍未实现。

脚本：../scripts/Inspect-PinnedLpcIo.py；完整指令：lpcio-pinned-disassembly.txt；分支轨迹与检查：lpcio-pinned-binary-index.json。
官方源代码已保存于 upstream/PawnIO 与 upstream/PawnPP，准确提交见 sources.json。
