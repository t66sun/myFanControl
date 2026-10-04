# PawnIO 2.0.1／2.2.0 切换核对

本轮仅离线检查安装器，未执行安装或卸载。主代理复核 2.0.1 安装器 SHA256 为 3a34b5df231f10f252c4b50d3c777534b2a2cec5e1df9466ab2dca401c068c44；PE 字符串实际包含以下帮助：

`Usage: PawnIOSetup.exe [-install] [-uninstall] [-unrestricted] [-debuginfo] [-silent]`

`-unrestricted Install unrestricted edition`

字符串明确要求测试签名，遇到既有安装要求先卸载。EXE 导入 CreateServiceW、ControlService、DeleteService、StartServiceW 等 SCM API；CAB 中没有 INF/CAT。证据支持它使用传统内核服务安装，不能把它当作目前 2.2.0 PnP 包的直接替换。

当前 2.2.0 实机记录为 ROOT\PAWNIO\0000、oem12.inf，准确包已通过 pnputil 导出，见 PAWNIO_BOOTSTRAP.md。

## 重启后的拟议切换路线

1. 确认测试签名在当前内核生效，HVCI 和阻止列表维持开启。
2. 核对原设备、服务、安装器登记和导出包；优先保留根设备实例，避免恢复时缺失设备节点。
3. 拟用 pnputil 禁用准确实例，再卸载准确原包；逐步核对设备仍保留、原服务已释放。不得使用宽泛匹配、强制删除、自动重启或直接覆盖 DriverStore。
4. 在原服务与安装登记冲突处理清楚后，拟执行已核对安装器的 `-install -unrestricted -silent`；检查实际加载路径和 SYS 哈希，不以安装器退出码代替驱动核对。
5. 实验后拟卸载传统服务，再将原导出 INF 通过 pnputil /add-driver /install 绑定至保留实例，启用实例并核对实际 2.2.0 文件和监控功能，然后恢复启动设置。

这些是待逐步验证的操作方案，尚不是已验证脚本。删除驱动包是否释放所有服务／安装登记、禁用的设备是否保留，以及安装器是否继续拒绝旧登记，都必须根据实际状态决定下一步。

`pnputil /add-driver /install` 仅对匹配的既有设备安装驱动，不能创建已删除的 ROOT\PAWNIO 根设备。因此不执行 remove-device 然后声称仅 add-driver 即可恢复；若实际卸载删除了设备节点，须先准备并核对根设备创建方式再继续。

## 当前停止位置

已写入测试签名启动设置，原驱动仍未改变，等待用户手动重启。若现在取消，只需恢复 testsigning 字段，尚不需要恢复驱动包。

参考：

- 官方 2.0.1：https://github.com/namazso/PawnIO.Setup/releases/tag/2.0.1
- PnPUtil 命令：https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax
- Driver Store：https://learn.microsoft.com/en-us/windows-hardware/drivers/install/driver-store

## 2026-10-02 重启后：复用现有 PawnIO 服务的评估

主代理报告已完成手动重启，当前内核代码完整性 flags 为 `0x00283603`，其中 TESTSIGNING 和 HVCI 位均已生效；2.2.0 驱动仍未更改。前文“等待用户手动重启”是重启前状态。此轮检查未接触驱动服务或硬件。

静态证据支持试用“禁用现有 PnP 节点、临时改同一个服务的 ImagePath、启动 2.0.1 x64 开发驱动”的可逆路线。它避免删除 PnP 节点、卸载 DriverStore 包、运行安装器或更改安装登记：

- 2.0.1 x64 `13wnIO.sys`（受限）和 `15wnIO.sys`（开发版）都导入 `IoCreateDevice`、`IoCreateSymbolicLink` 和 `IoDeleteDevice`；未见当前 2.2.0 PnP 路径使用的 `IoGetDeviceProperty`、`IoAttachDeviceToDeviceStack`、`IofCallDriver`。2.0.1 CAB 文件表没有 INF/CAT。其安装器则导入 SCM 的 `CreateServiceW`、`StartServiceW`、`ControlService`、`DeleteService`。这些证据表明 2.0.1 是由 SCM 启动、在 DriverEntry 创建设备对象的传统驱动。
- 2.2.0 本地上游源码 `research/upstream/PawnIO/PawnIO/src/driver.cpp` 设置 `DriverExtension->AddDevice` 和 `IRP_MJ_PNP`，在 `AddDevice` 中创建设备并附加到 PDO；INF 将 `Root\PawnIO` 节点绑定到 `PawnIO` kernel service。目标节点是 `ROOT\PAWNIO\0000`。实机导出记录显示原镜像路径为 `C:\Windows\System32\DriverStore\FileRepository\pawnio.inf_amd64_a72a2f969b8b7496\PawnIO.sys`，SHA256 为 `fca6e7d58b0cf38dbb913a2b9e532f48629145d395f454b16a9f58e97b8d3940`。
- Windows 的 `sc.exe config` 支持修改驱动服务的 `binpath`，`StartService` 也支持启动 driver service。参考 [sc.exe config](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sc-config)、[ChangeServiceConfig](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-changeserviceconfiga)、[Service Startup](https://learn.microsoft.com/en-us/windows/win32/services/service-startup)。

该路线仍有一个必须以运行状态判定的限制：PnPUtil 禁用设备不等于移除节点。2.2.0 的 `IRP_MN_STOP_DEVICE` 处理只向下传递 IRP，设备对象和符号链接在 `IRP_MN_REMOVE_DEVICE` 才删除。因此禁用后，2.2.0 驱动或服务可能仍处于加载状态；静态源码不能证明它会及时卸载。只在 `PawnIO` 服务状态确认为 `STOPPED` 后才改 ImagePath；若未停止、路径修改失败或候选无法启动，立即停止流程并重新启用原节点，不能强制卸载或删服务。

建议的临时步骤（尚未执行）：保存 `sc qc PawnIO` 的原始配置；禁用准确节点 `ROOT\PAWNIO\0000`；确认节点仍存在且服务已停止；将 `PawnIO` 的 `binPath` 临时指向 `artifacts\drivers\PawnIO-2.0.1\extracted\15wnIO.sys`；启动后核对实际镜像 SHA256，再只运行无硬件访问模块。候选文件 SHA256 为 `ac670293a260e48d4199a950ec6ead52d0b6a6444b5f80920b6c27792fc69cd1`。若服务未停止，不能假定 SCM 的 `sc config` 会替换已加载映像。

恢复建议：停止并确认候选服务已停止；把 `binPath` 恢复为本机保存的确切原值；重新启用仍保留的 `ROOT\PAWNIO\0000`；核对 PnP 绑定路径、原镜像哈希、PawnIO 可用性与风扇监测。原包完整导出在 `artifacts/diagnostics/pawnio-bootstrap-20261002-143938-347/original-driver-package`（`PawnIO.sys`、`pawnio.inf`、`PawnIO.cat`）；即使此路线不删除包，这份导出仍是恢复备份。不要直接改写 DriverStore 内文件。

上述路径通过 SCM 复用服务的可行性来自官方 Windows 服务 API 文档与本地静态 PE/源码证据；尚无此驱动组合的运行验证。首个停止门是禁用设备后 `PawnIO` 是否能到达 `STOPPED`。无论成功或失败，测试模块仍须无 native、无端口 I/O、仅返回 0。

## 2026-10-02 首次服务复用实测

日志位于 `artifacts/diagnostics/pawnio-dev-load-20261002-145948-836` 与 `artifacts/diagnostics/pawnio-dev-restore-20261002-150254-143`。当前内核 flags 为 `00283603`，HVCI 保持启用。操作仅禁用 `ROOT\PAWNIO\0000`、修改现有 `PawnIO` 服务的镜像路径并启动；未删除驱动包、未移除设备、未访问硬件。

禁用设备和 `sc.exe config`、`sc.exe start PawnIO` 均返回成功，SCM 报告 `RUNNING`。服务镜像路径已规范化为 `\??\C:\dev\myfancontrol\artifacts\drivers\PawnIO-2.0.1\extracted\15wnIO.sys`，而初版脚本按 DOS 路径字符串比较，因路径前缀差异触发保护性中止。因此本轮尚未完成候选镜像哈希核对，也未尝试加载无硬件模块。`sc.exe stop PawnIO` 返回 1052（该服务类型不接受此控制请求）。

恢复记录显示，针对准确服务路径调用 `NtUnloadDriver` 返回 `STATUS_SUCCESS`；随后原 `ImagePath` 已恢复为 `\SystemRoot\System32\DriverStore\FileRepository\pawnio.inf_amd64_a72a2f969b8b7496\PawnIO.sys`，准确节点重新启用，绑定仍为 `ROOT\PAWNIO\0000` / `oem12.inf` / 2.2.0.0。主代理报告原监控驱动已恢复。由此验证了此次原地路径切换及回滚可行；后续测试须先修复路径规范化比较，再独立核对实际候选哈希和运行无硬件模块，任何失败都按已实测的回滚顺序恢复。
