# 增强传感器

很多电脑上，Windows 不提供 CPU 温度或 CPU 功耗：台式机常常没有可用的 ACPI 热区，部分 AMD 电脑没有能量计。读取这两项需要访问处理器的寄存器，只有内核驱动能做到。增强传感器借助开源驱动 [PawnIO](https://pawnio.eu)，由 BrimDeck 的传感器服务直接从处理器读取。

位置：设置 › 系统监控 › 增强传感器。

## 可以多读到的数据

| 读数 | Intel | AMD（Zen 系列，family 17h–1Ah） |
| --- | --- | --- |
| CPU 温度 | 封装温度 | Tctl/Tdie |
| 分区温度 | 每个核心 | 每个 CCD |
| CPU 功耗 | 封装功耗；内存功耗 | 封装功耗 |

监控页的 CPU 温度提示会标明来源，例如“（处理器传感器）”。实际能读到哪几项取决于处理器型号。独立显卡和硬盘的读数不受影响，它们本来就不需要 PawnIO。

## 启用步骤

1. **安装 PawnIO**：点击“安装”，BrimDeck 会打开一个命令窗口运行 `winget install --id namazso.PawnIO --exact`，安装程序会请求管理员确认。没有 winget 时会打开 PawnIO 的官方下载页面。已经装过 PawnIO 的电脑（例如装过 FanControl 或 LibreHardwareMonitor）会直接显示“已安装”，不需要再装。
2. **启用传感器服务**：点击“启用”，BrimDeck 会下载传感器服务和 PawnIO 官方模块，Windows 请求一次管理员确认，之后不再需要。

## 安全说明

- 传感器服务装在 `C:\Program Files\BrimDeck Sensors`，只有管理员能修改这个目录。服务以系统账户运行，因为 PawnIO 只允许系统账户和管理员使用。
- 服务只读取固定的几个温度和能量寄存器，不写入任何寄存器，也不提供读取其他寄存器的功能。
- 下载的服务程序必须与当前 BrimDeck 版本发布时记录的 SHA-256 一致；PawnIO 模块来自 PawnIO 官方发布页，同样核对 SHA-256，PawnIO 驱动本身也只加载带官方签名的模块。
- 服务只在监控页或收起行需要 CPU 温度、功耗时运行，空闲约一分钟后自行退出。BrimDeck 通过本机命名管道读取数值，只有本机已登录的用户可以连接。

## 停用与卸载

- **停用传感器服务**：在增强传感器页点击“停用”，会删除服务和 `C:\Program Files\BrimDeck Sensors`，需要一次管理员确认。卸载 BrimDeck 时也会一并删除。
- **卸载 PawnIO**：在同一页点击 PawnIO 旁的“卸载”，或在 Windows 设置的“应用”中卸载。FanControl、LibreHardwareMonitor 等软件也可能使用 PawnIO，卸载后它们也会受影响。

## 未启用时

BrimDeck 仍然读取 Windows 提供的 CPU 温度（ACPI 热区）和功耗（能量计）。如果正在运行以下软件，也会从中读取 CPU 温度和功耗：

- **HWiNFO**：需要在其设置中打开“共享内存支持”（Shared Memory Support）。免费版每次开启 12 小时。
- **LibreHardwareMonitor**：需要在“选项”中打开“远程网页服务器”（Remote Web Server），使用默认端口 8085。BrimDeck 只连接本机地址。

BrimDeck 启动时、监控页打开时会查找这两个软件，没找到时在随后几分钟内再找几次。
