# 本地 Pawn 模块编译工具

已取得官方 Pawn 4.1.7487 Windows 工具包。下载来源 https://www.compuphase.com/pawn/pawn-4.1.7487.exe ，由官方页面 https://www.compuphase.com/pawn/pawn.htm 链接提供。

下载为 8,441,614 字节，SHA256 106527794a995acd89881ca55985450af6ecde0376f525e7aa5f49314556cb40。网站未提供本轮核对过的发布哈希，此处哈希是本地固定值。TLS 校验保持开启，129 个 HTTP Range 均核对 Content-Range 和同一 ETag，下载证据在 .tools/pawn-compiler/pawn-4.1.7487.download.json。

整包读取进程长时间不产出文件；单独范围请求已实测成功后，主代理核对 PID 3088 和启动时间，仅结束该下载进程，会话 88746 退出 1。改用范围下载的会话 10118 退出 0，耗时约 68 秒。没有因观察超时重复发起同一任务。

## 提取和编译

安装包是 NSIS solid LZMA。尝试取得 7-Zip MSI 时遇到实际 TLS 超时，会话 26792 已退出 1，未安装工具。随后直接使用 Python 标准库 lzma 解码本地 NSIS 数据并遍历块，以 PE 版本资源选出唯一 pawncc.exe；无需运行安装包。

scripts/Extract-PawnCompiler.py 固定校验整个工具包和编译器哈希。编译器位于 .tools/pawn-compiler/bin/pawncc.exe，SHA256 2136ef8776744e88c72bafc6e0247bce4f10bd2283d1a5a46b98d52a2da2d4a1，版本 4.1.7487，仅依赖系统 USER32/KERNEL32。

scripts/Build-PawnModules.ps1 使用 -C64、-O1、-p，以及项目保存的 PawnIO.Modules include。Windows 编译器的最小参数组合已实际编译成功；Pmc2Status 有上游 include 的旧式 forward 声明警告 218，没有编译错误。

生成的 AMX 与封装位于 artifacts/pawn-modules。封装使用 512 字节无效签名，仅用于已授权的开发驱动实验，不宣称为正式签名模块。

- NoHardwareMain：无 native、无 public，main 返回 0；只用于模块加载验收。
- Pmc2Status：native 为 io_in_byte/get_arch/debug_print，唯一导出只读取固定 6C。加载 main 只做架构检查，不读取端口；调用导出前由宿主复核硬件身份、PMC2 配置并取得 ISA 互斥。

编译成功不等于驱动加载成功。实时加载证据另见管理员实验 report.json；查询与控制尚未验收。

2026-10-02 更新：NoHardwareNative 的 get_arch/debug_print main 已实际加载成功。Pmc2Status 加入同一 main 模式后实际导出读取6C返回00；哈希与导出表见 pawn-compiled-modules.json。新增Pmc2Query只有固定查询导出，内核逐项检查缓冲大小及D5、12/18/19范围，尚待实机查询。
