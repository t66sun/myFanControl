# 替代传输：官方 PawnIO 2.0.1 开发版

用户已明确要求不提交 Issue。本项目未公开发布支持请求。

已从官方 GitHub release 取得 PawnIO 2.0.1 和 2.1.0 安装包，并与发布资产 SHA256 校对。只提取资源和驱动，未运行安装器、未加载驱动。复用脚本 scripts/Extract-PawnIoPackage.py；CAB 中有重复名称，检查副本只修改文件表中的名字，未修改驱动字节。

2.0.1 的 15wnIO.sys 是 x64 开发版，SHA256 ac670293a260e48d4199a950ec6ead52d0b6a6444b5f80920b6c27792fc69cd1；包含 [PawnIO] Signature check result 字符串及实际代码引用。该路径调用签名验证、输出结果，随后直接进入分配流程，没有正式版的签名拒绝分支。尚未运行验证自定义模块加载。维护者推荐 2.0.1/2.1.0 或自行构建来避免 2.2.0 开发版问题：https://github.com/namazso/PawnIO.Setup/issues/11 。

开发版证书为 WDKTestCert，有效期到 2035 年，普通 Authenticode 校验报告根证书不受信任。正式 x64 版本 13wnIO.sys 有 Microsoft 签名，但不能据此获得自定义模块能力。2.1.0 包提取出的开发版候选是 ARM64，未用于本机。

本机 NtQuerySystemInformation 的代码完整性查询成功，flags=3401：测试签名未启用，HVCI 已启用。注册表 SecureBoot 值 0、驱动阻止列表值 1。没有修改这些设置。Microsoft 的测试驱动加载方式需要 TESTSIGNING 和重启；HVCI 要求驱动具有测试签名，不能使用完全无签名的二进制：https://learn.microsoft.com/en-us/windows-hardware/drivers/install/the-testsigning-boot-configuration-option 。能否在本机加载该开发版仍须验证。

已保存当前实际安装的 Microsoft 签名 PawnIO 2.2.0 SYS/INF/CAT 到 artifacts/drivers/PawnIO-installed-backup。这个目录只用于保留恢复材料，不是已验证的一键恢复方案。LHM 仓库附带安装包实际上是 2.1.0，不能当作 2.2.0 恢复安装器。

无硬件访问的加载测试模块 NoHardwareMain.amx 及封装位于 artifacts/drivers/PawnIO-2.0.1：main 仅返回 0，无 native 导入。其文件头和三条 main 指令已核对，尚未实际加载。原版 SDK 工具已放在 tools-x64，并核对其 help 和离线 sign 命令；没有执行 test/interactive。启动开发驱动成功后，先以这个模块验证自定义模块机制，再实现和验证仅 6C 状态读取。

拟议操作：由用户明确接受测试签名设置及重启；保存当前启动选项，再切换至已核对的开发版，检查实际绑定的 x64 文件哈希；先执行无硬件模块测试，成功后再进行单端口读取。不关闭 HVCI 或驱动阻止列表，不修改风扇目标。设置和加载尚未获实际验证。
