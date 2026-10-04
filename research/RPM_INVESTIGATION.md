# 转速接口与温度权限核对

日期：2026-09-30。所有检查和实机执行均由主代理完成。

## CPU 温度设备访问

Get-PawnIoAccess.ps1 仅以 LibreHardwareMonitor 上游使用的参数打开已安装的 PawnIO 设备，然后立即关闭，没有 DeviceIoControl、模块加载、服务操作或安装。

沙箱内外普通权限各运行一次：PawnIO 2.2.0.0 服务运行，CreateFile 均返回 Win32 5（Access is denied）。因此所需设备访问确实被拒绝；管理员下能否成功读温度仍未实测，不自动重复此前取消的 UAC 请求。

PawnIoAccessReader 将设备打开结果加入 --probe JSON，界面显示具体权限错误。发布后实测 2 轮，win32Error=5、isAdministrator=false，CPU 温度缺失，退出码 2；固件查询成功，模式为性能。真实数据的布局验证通过，主代理查看最小窗口图像。尚未验证桌面交互。

证据位于 artifacts/diagnostics 的 pawnio-access.json、pawnio-access-unrestricted.json、pawnio-integrated-probe.json。

## 本机 WMI GUID

Index-AcpiWmi.py 解析 DSDT 和 20 张 SSDT 中全部 7 个字面量 _WDG 缓冲区，共 25 条记录。校验声明长度、20 字节边界，并在原始 .dat 中逐缓冲区验证字节序列。注释中的花括号不会误作缓冲区结束。GUID 采用 bytes_le，记录 Object ID、flags、instance count、事件编号和 handler 候选；这不等于方法实测。

GameZone（887B54E3-DDDC-4B2C-8B88-68A26A8835D0）、16p Gen4 风扇接口（777B54E3-DDDC-4B2C-8B88-68A26A8835D0）、OtherMethod（DC2A8805-3A8C-41BA-A6F7-092E0089CD3B）在这些注册记录中均未发现，详见 acpi-wmi-index.json。这支持此前 CIM 结果，不能推导本机 EC 一定无法调速。

实际存在的 WMSR 是 Super Resolution；WMSK 返回多项能力；WMDE/WMTE 处理 PDAT 与事件。ssdt7/8 方法经 SSMP=0xC2 进入 SMI 服务，是 BIOS 配置路径，目前未发现 RPM 协议。没有猜测参数或试写这些方法。

## 新增源码

- [thinkbook16p-fan-control](https://github.com/onurosen/thinkbook16p-fan-control) 的 [逆向记录](https://github.com/onurosen/thinkbook16p-fan-control/blob/main/docs/REVERSE_ENGINEERING.md) 描述 21J8 的 777B... WMA0 和 0x68/0x6C 端口控制。本机没有该 GUID，不照搬写序列。
- [Thinkbook14PAutoFanController](https://github.com/markgoo/Thinkbook14PAutoFanController) 配置以 0xC830/0xC831、0xC832/0xC833 读取 RPM，以 0xC83C/0xC83D 设置目标。源码初始化还写 EC RAM 0x1060 bit7，不能直接当只读工具运行。本机芯片与银行映射尚未确认，不使用这些地址写入。
- 两个仓库已克隆，仅阅读源码，未运行其二进制、初始化函数或安装驱动。实际 HEAD 已保存到 sources.json；Git 所有者差异仅用单次命令 safe.directory 精确指定工作区仓库，没有改全局配置。

## 仍缺的证据

ssdt13 的 _TMP 固定返回 0x0BB8/0x0BC2（约 26.85/27.85 摄氏度），不能代替 CPU 温度。RPM 遥测、任意转速控制字、有效范围和自动释放协议仍未验证。已证实的 MMC 性能/安静往返不能代替完整目标。

后续继续核对本机 EC 芯片、银行映射和 OEM 固件路径；物理调速效果必须以真实读数验证。
