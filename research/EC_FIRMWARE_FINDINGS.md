# HYCN42WW 内嵌 EC 固件静态索引

## 来源与范围

官方包来源、签名和哈希见 [firmware-package-source.json](firmware-package-source.json)。仅提取数据，未执行 BIOS 更新程序、EFI 程序或驱动。

`WinHYCN42WW.fd` 的 SHA256 为 `d6fd2eb9cdbd49164d70eedf5a049eba8a4b8a522e63891d3a1e6c5e5b041056`。更新配置 `[UpdateEC]` 中 Flag=1、BIOS_Only=0、EC_Only=0、EC_Path 为空；注释描述合并文件更新。这只是包的配置，不证明运行时接口。

## 候选区域

在文件偏移 `0x9E4D90` 处找到候选 EC 程序区，长度暂取 `0x20000`（128 KiB），结束处为校验和有效的 UEFI FV 头。开头 `02 00 70` 与 8051 的 LJMP 0x0070 编码相符，后续包含类似中断向量、RET 和初始化序列。指令编码依据 [Keil 8051 官方手册](https://www.keil.com/support/man/docs/is51/is51_opcodes.asp?bhcp=1)。这是架构推断。后续本机身份诊断已返回 0x5571（见 EC_IDENTITY_DIAGNOSTIC.md）；候选镜像的银行布局及其与当前运行内容的对应仍未确认。

区域中的关键字符串：

| 相对偏移 | 内容 |
| --- | --- |
| 0x50 | ITE EC-V14.4 |
| 0x802B | Project:NB5979 |
| 0x803A | ECVer:01.42.00.00 |
| 0x8052 | HYEC42WW |
| 0x805C | Date:2023/01/13 |

候选区域保存为 `artifacts/firmware/payload/HYEC42WW-candidate.bin`，SHA256 为 `73e7dce2a408fd8aae851450259f58e270a1482306e80523e0f45ac19c8a558c`。提取脚本先核对源镜像哈希、向量、版本字符串和下一 FV 头校验和，防止对不同版本误用固定偏移。

## 地址索引与限制

[Index-ThinkBookEcFirmware.py](../scripts/Index-ThinkBookEcFirmware.py) 保存所有原始 `90 hi lo` 字节匹配至 [ec-firmware-index.json](ec-firmware-index.json)。在 8051 中该编码对应 MOV DPTR,#immediate，但数据也可能匹配；索引未完成指令边界、控制流、银行或动态地址计算分析，不能视为有效寄存器清单。

该区域没有直接匹配其他 ThinkBook 项目的 C830/C831/C832/C833/C83C/C83D 或 1060 地址加载模式。此结果不证明这些地址不受支持，也不能据此选择替代写入地址。

下一步追踪 EC 命令处理与 RAM 映射，关联 ACPI 的 RDER 邮箱协议、固件风扇模式及 RPM 数据。控制协议和恢复路径确认前，不进行新的 EC 写入。
