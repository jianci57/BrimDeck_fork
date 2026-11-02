# 数据与隐私

BrimDeck 仅读取应用已有的登录状态与用量记录，不会修改、续期或注销它们的登录，也不会上传会话内容或收集使用数据。

## 读取的数据

| 工具 | 配额 | Token 用量与费用 |
| --- | --- | --- |
| Claude | 读取 Claude Code 的登录（`%USERPROFILE%\.claude\.credentials.json`），不可用时改用 Claude 桌面版的登录，向 Anthropic 查询账户用量。账户同时属于多个组织（例如个人 Pro 与 Team）时，用桌面版为各组织保存的登录分别查询。每一行可以单独关闭"联网获取额度"：关闭的行不读取登录、不联网，只显示 Claude 桌面版在本机记录的额度（`plan-usage-history.json`） | 读取 Claude Code 的本地会话记录（`projects\**\*.jsonl`），不含 Claude 桌面版的普通聊天 |
| Codex | 读取 `%USERPROFILE%\.codex\auth.json` 中的登录，向 OpenAI 查询账户用量；查询失败时显示会话记录中的配额，并标明可能已过时 | 读取本地会话记录（`sessions` 与 `archived_sessions`） |
| Antigravity | 从正在运行的 Antigravity 桌面版的本机服务（仅限 127.0.0.1）读取 | 读取 `%USERPROFILE%\.gemini` 下桌面版与 CLI 的本地会话数据库 |
| Cursor | 以只读方式从 `%APPDATA%\Cursor\User\globalStorage\state.vscdb` 读取登录，向 Cursor 查询账户用量 | 向 Cursor 查询账户用量明细，包含该账户在其他设备上的用量 |
| Grok Bot | 读取 Grok Bot 桌面版自己的登录（`%APPDATA%\Grok Bot\sand-secrets.json`），向 Cursor 查询该账户的每周额度。每一行可以单独关闭"联网获取额度"：关闭的行不读取登录、不联网，只显示 Grok Bot 桌面版在本机缓存的每周额度（`%APPDATA%\Grok Bot\sand-client-persistence`） | 不提供 |
| ZCode | 读取 ZCode 的登录，查询 Coding Plan、Start Plan 与 MCP 每日额度 | 读取本地请求记录（`%USERPROFILE%\.zcode\cli\db\db.sqlite`） |
| DeepSeek Harness | 读取 DeepSeek Harness 的登录（`%USERPROFILE%\.dsh\.credentials.yaml`），向 DeepSeek 开放平台查询账户余额与累计消费金额；未登录账户时，使用其中保存的 API 密钥查询余额 | 读取本地会话记录（`%USERPROFILE%\.dsh\sessions`） |
| Kimi Code | 读取 Kimi Code 的登录（`%USERPROFILE%\.kimi-code\credentials\` 下的 `kimi-code.json`，海外版为 `kimi-code-env-…json`）、服务地址与站点设置（`config.toml`，以及可选的 `region` 文件），向所选站点（api.kimi.com 或 api.kimi.ai）查询套餐额度与会员等级；不续期登录，不修改 Kimi Code 的任何文件 | 读取本地会话记录（`%USERPROFILE%\.kimi-code\sessions`） |
| OpenCode | 以只读方式读取 OpenCode 数据库（`%USERPROFILE%\.local\share\opencode\opencode.db`）中的 OpenCode Console 登录，没有时读取同一目录下 `auth.json` 中的 API 密钥，向 opencode.ai 查询 OpenCode Go 额度；不续期登录，不修改 OpenCode 的任何文件 | 以只读方式读取同一数据库中的模型请求记录 |
| 智谱 GLM、Z.ai GLM、DeepSeek、Kimi Code API、OpenCode Go、OpenRouter、New API、Sub2API、自定义 | 使用在设置中填写的密钥与站点地址；Kimi Code API 的密钥只发往 api.kimi.com 与 api.kimi.ai，读取的额度不写入 `kimi-quota.json`；OpenCode Go 的密钥只发往 opencode.ai；OpenRouter 的密钥只发往 openrouter.ai。密钥只通过 HTTPS 或发往本机地址；New API、Sub2API、自定义来源打开“允许 HTTP”后，也会通过 HTTP 以明文发送 | 不提供 |

- 设置了 `CLAUDE_CONFIG_DIR`、`CODEX_HOME`、`ZCODE_HOME`、`DSH_HOME` 或 `KIMI_CODE_HOME`（须为绝对路径）时，从该目录读取。OpenCode 按 `XDG_DATA_HOME` 与 `OPENCODE_DB` 确定数据库位置。
- 这些账户接口与本地数据格式并非公开的稳定接口。无法读取时，面板显示实际状态，不会显示虚构的数值。

## 本机保存的文件

所有文件保存在 `%LOCALAPPDATA%\BrimDeck`：

| 文件 | 内容 |
| --- | --- |
| `settings.json` | 设置，不含任何登录令牌或 API 密钥 |
| `secrets.dat` | 手动填写的 API 密钥，使用 Windows 的加密功能按当前用户加密 |
| `model-prices.json`、`manual-model-prices.json` | 同步的模型单价、手动添加的单价 |
| `official-prices.json` | 从 OpenRouter 同步的模型厂商官方渠道单价，只包含记录中用到的模型，以及 Kimi Code 计费所需的 Kimi K3、K3.1 Pro 与 K3.1 Pro Highspeed。没有这个文件时，读取旧版本保存的 `moonshot-prices.json` |
| `claude-quota.json` | 最近一次联网读取的 Claude 额度（各项百分比、重置时间与订阅名称），重启后接口被限流时用来继续显示 Fable 等额度；不含登录令牌；账户与组织仅以其 ID 的单向哈希记录（不保存 ID 本身），每个组织一条，只在同一账户登录时使用；超过 7 天不再使用，登录失效或退出登录时删除 |
| `kimi-quota.json` | 最近一次联网读取的 Kimi Code 额度（各项百分比、重置时间、加油包与会员等级），登录过期或接口暂时不可用时继续显示，重启后同样适用；不含登录令牌与其他账户资料；账户仅以其 ID 连同服务地址的单向哈希记录（不保存 ID 本身），只在同一站点的同一账户登录时使用；超过 7 天不再使用，退出登录或账户未订阅 Kimi Code 套餐时删除 |
| `update-prompt.txt`、`updates\` | 已提示过的版本号、下载的安装包或免安装压缩包（免安装版更新时还有解压出的新版本文件） |
| `sensor-stops.json`、`sensor-pending.json` | 系统监控因电脑卡住而停止读取的项目（硬盘或显卡的型号、主板型号与 BIOS 版本），以及正在读取的项目，用于在下次启动时发现上次卡住的读取；在设置中重新开启后删除对应记录 |
| `crash.log` | 错误记录，超过 1 MB 时清空 |

## 联网与安全

- 仅连接以下地址：各工具的官方接口、你在设置中填写的站点、OpenRouter 模型目录及记录中用到的模型的渠道列表（同步模型单价，只发送模型名称）、歌词服务（LRCLIB、网易云音乐、QQ 音乐、酷狗音乐）、播放器提供的封面地址，以及 GitHub Releases（检查更新；启用增强传感器时下载传感器服务与 PawnIO 官方模块）。
- Claude 桌面版的数据目录包括 `%APPDATA%\Claude`、商店版 `%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude` 和 `%LOCALAPPDATA%\Packages\AnthropicPBC.Claude_*\LocalCache\Roaming\Claude`，以及 `%LOCALAPPDATA%\Claude-Data\<安装名>`。
- 读取的登录令牌仅在内存中使用，不写入磁盘；Claude 桌面版与 Grok Bot 桌面版的登录缓存使用当前用户的 Windows 加密功能在内存中解密。
- 自定义脚本在沙盒中运行，无法访问文件、进程或 .NET 对象，限制详见[支持的工具](ai-tools.md#自定义脚本)。
- 系统监控的读数（包括温度和硬盘空间）只在本机读取和显示，不保存、不上传。硬盘空间来自各盘符的文件系统，盘符所在的物理硬盘通过 Windows 的卷信息查询。温度默认通过 Windows 自带的接口读取：CPU 用性能计数器中的 ACPI 热区，GPU 用显卡驱动报告的数据（显卡内核接口没有时，改用 NVIDIA、AMD、Intel 驱动自带的接口），硬盘以不可读写的方式打开后查询其温度；不需要管理员权限，不安装驱动。如果正在运行 HWiNFO（已开启共享内存）或 LibreHardwareMonitor（已开启远程网页服务，只连接本机 localhost:8085），也会从中读取 CPU 温度和功耗。启用[增强传感器](enhanced-sensors.md)后，CPU 温度和功耗由 BrimDeck 的传感器服务通过 PawnIO 驱动从处理器读取。
- 整机功耗和本机电池同样只在本机读取和显示，不保存、不上传，且只在系统监控页或收起行选了该读数时读取：CPU 功率来自 Windows 能量计（Energy Meter）的性能计数器，NVIDIA 显卡功率来自显卡驱动自带的 nvidia-smi.exe（只在显卡有负载、且页面或收起行要显示功耗时由 BrimDeck 启动，随 BrimDeck 退出而结束；显卡空闲时不读取，以免唤醒休眠的独立显卡），电池电量、充放电功率和容量来自 Windows 的电池接口，电源模式和节电模式来自 Windows 的电源接口；不需要管理员权限，不安装驱动。
- 律动条所用的声音仅在内存中分析，不会保存。

## 网易云音乐完整控制

- 完整控制需要网易云开启本机调试端口。通过 BrimDeck 启用时，使用的端口为 127.0.0.1:21631。
- 端口开启期间，本机其他程序和浏览器网页可能借此操控网易云，读取其页面内容与登录状态。
- BrimDeck 连接前会确认端口仅监听本机地址且属于网易云进程，但无法消除端口本身带来的风险。
- 通过这个端口，BrimDeck 读取正在播放的歌曲、进度、播放模式和当前歌曲是否已喜欢，只在您点击按钮时让网易云执行对应的操作。点击红心时，由网易云自己把歌曲加入或移出您账户中的"我喜欢的音乐"。为显示红心，BrimDeck 只取得网易云是否已登录、当前歌曲是否已喜欢这两项结果，不读取或保存您的账户信息和其他歌曲。
- 临时启用时，端口会一直开到网易云以普通方式重新启动为止：退出 BrimDeck、完整控制连接失败（BrimDeck 改为只读取播放信息）都不会关闭它。
- 打开"始终启用完整控制"开关后，BrimDeck 会修改网易云安装目录中 `libcef.dll` 的 2 处字节，修改后网易云每次启动都会打开这个调试端口。端口会一直存在，直到在"设置 › 音乐 › 播放器 › 网易云音乐"中关闭"始终启用完整控制"后网易云再次启动，或网易云更新换掉这个文件；退出 BrimDeck 不会关闭它。
- 请仅在可信的环境中启用。

## 酷狗音乐完整控制

- 完整控制会修改酷狗安装目录中 `libcef.dll` 的 9 处字节，修改后酷狗每次启动都会打开本机调试端口 127.0.0.1:12233。
- 端口开启期间，本机其他程序可能借此操控酷狗，读取其页面内容与登录状态。
- BrimDeck 连接前会确认端口仅监听本机地址且属于酷狗进程，但无法消除端口本身带来的风险。
- 通过这个端口，BrimDeck 只读取正在播放的歌曲、播放状态、进度、时长、封面地址和歌曲的哈希值，只在您点击播放、暂停、上一首或下一首时让酷狗执行对应的操作，不读取或保存您的账户信息。
- 歌曲的哈希值和时长会发送给酷狗的歌词服务，用于查询这首歌的歌词。
- 端口会一直存在，直到在"设置 › 播放器 › 酷狗音乐"中关闭"始终启用完整控制"后酷狗再次启动，或酷狗更新换掉这个文件；退出 BrimDeck 不会关闭它。请仅在可信的环境中启用。
