# 支持的工具

AI 用量页的每一列可以在"设置 › 配额与统计"中分别配置：

- **配额来源**：决定这一列显示的套餐、配额使用率与重置倒计时。
- **统计应用**：决定这一列底部"今日"与"七日"的 Token 用量、费用估算，以及模型明细。
- **显示名称与颜色**：可以自定义名称、主题色、预警色与告警色。

同一个工具可以出现在多列中，读取时只请求一次，合计也不会重复计算。

| 工具 | 配额 | Token 用量与费用 | 需要的准备 |
| --- | :---: | :---: | --- |
| Claude | ✓ | ✓ | 在本机登录 Claude Code 或 Claude 桌面版。用量统计来自 Claude Code |
| Codex | ✓ | ✓ | 在本机登录 Codex |
| Cursor | ✓ | ✓ | 在本机登录 Cursor |
| Grok Bot | ✓ | | 在本机登录 Grok Bot 桌面版 |
| Antigravity | ✓ | ✓ | 读取配额时需要 Antigravity 桌面版正在运行 |
| ZCode | ✓ | ✓ | 在本机登录 ZCode |
| DeepSeek Harness | ✓ | ✓ | 在 DeepSeek Harness 桌面版或本地 Web 版中登录 DeepSeek 账户，或在其中保存 API 密钥 |
| Kimi Code | ✓ | ✓ | 在本机登录 Kimi Code（CLI、桌面版或 VS Code 扩展） |
| Pi | | ✓ | 在本机使用 Pi Coding Agent（读取会话 Token 与费用统计） |
| OpenCode | ✓ | ✓ | 配额：在本机的 OpenCode（CLI 或桌面版）中登录 OpenCode Console，且工作区订阅了 OpenCode Go。用量统计只需在本机使用 OpenCode |
| Command Code | ✓ | ✓ | 在本机登录 Command Code（CLI 或桌面版） |
| WorkBuddy | ✓ | ✓ | 配额：在设置中通过浏览器登录 WorkBuddy 官网。用量统计只需在本机使用 WorkBuddy 国内版桌面应用 |
| WorkBuddy AI | ✓ | ✓ | 配额：在设置中通过浏览器登录 WorkBuddy 国际版官网。用量统计只需在本机使用 WorkBuddy AI 桌面应用 |
| Xiaomi MiMo 桌面订阅 | ✓ | ✓ | 配额：在设置中通过浏览器登录小米账号。用量统计只需在本机使用 MiMo Code CLI 或 Xiaomi MiMo 桌面版 |
| Xiaomi MiMo | ✓ | | 在 Xiaomi MiMo 桌面版或 MiMo Code CLI 中关联开放平台 API 密钥或 Token Plan |
| 智谱 GLM、Z.ai GLM | ✓ | | 在设置中填写 API 密钥 |
| Xiaomi MiMo API | ✓ | | 在设置中填写小米 MiMo 开放平台按量计费 API 密钥 |
| Xiaomi MiMo Token Plan | ✓ | | 在设置中填写小米 MiMo Token Plan 密钥 |
| DeepSeek | ✓ | | 在设置中填写 API 密钥 |
| Kimi Code API | ✓ | | 在设置中填写 Kimi Code API 密钥 |
| Kimi 开放平台 | ✓ | | 在设置中填写 Kimi 开放平台 API 密钥 |
| OpenCode Go | ✓ | | 在设置中填写 OpenCode Console API 密钥 |
| Command Code API | ✓ | | 在设置中填写 Command Code API 密钥 |
| OpenRouter | ✓ | | 在设置中填写 OpenRouter API 密钥 |
| New API、Sub2API 中转站 | ✓ | | 在设置中填写站点地址和密钥 |
| 自定义 | ✓ | | 编写一段 JavaScript 脚本 |

手动填写的来源与 Grok Bot 没有本地用量记录，"今日"和"七日"可以另选一个工具统计。

## 费用估算

记录本身带有金额时（例如 Cursor、OpenCode 与 Command Code），直接使用该金额。其他记录按 OpenRouter 模型目录中的单价估算。

目录中的单价通常取自最便宜的服务商，可能与模型厂商的官方价格不同。例如 GLM-5.3 在目录中的输入与输出单价为每百万 Token 0.07 与 1.4 美元，Z.AI 官方为 1.4 与 4.4 美元。因此，对记录中用到的每个模型，BrimDeck 会查询 OpenRouter 上该模型厂商自己的渠道，并改用该渠道的单价；厂商没有渠道的模型，沿用目录单价。厂商渠道的单价每个模型每天查询一次，并保存在本机；查询失败时沿用上次保存的单价。手动添加的单价优先。

## 配额环与低峰时间

在"设置 › 配额与统计 › 应用"中展开对应的应用行，可以填写：

- **目标金额**：按 API 计费的来源（例如 DeepSeek、New API、Sub2API），可以为每一项余额或扣费分别填写目标金额，以该金额为 100% 显示比例：余额按剩余比例计算，例如目标为 100、剩余 20 时显示已用 80%；扣费按已花比例计算。账户余额的目标金额新添加的行默认为 100，其他项目默认不设置。配额环、展开面板的进度条、预警颜色与提醒都按目标金额计算；留空时只显示金额。
- **配额环额度**：选择收起时这个应用的配额环显示哪些额度。多环样式下可以分别选择外环、中环、内环，每个环都可以设为“不显示”，其余的环保持原来的位置；相同的额度不会重复显示；默认情况下，额度按周期从短到长依次放入外环、中环、内环，没有周期的额度（例如工具调用次数）排在最后，例如智谱为 5 小时、每周、工具调用次数；缺少其中某一项时，后面的额度依次向外移；每个环固定显示各自的额度，不会因为另一项用得更多而改变；点击“恢复默认”可回到默认的显示方式。按 API 计费的来源也可以选择赠送余额、密钥余额、今日扣费等项目，账户余额默认排在最前，比例按上一项的目标金额计算。单环样式下，有账户余额的来源默认显示账户余额，其他来源默认显示已用比例最高的一项。某项额度暂时没有数据时，它的环只显示底色。环的样式、显示已用还是剩余额度、是否显示首字母，在“配额与统计”页“应用”上方的“配额显示”小节中设置；选择剩余额度时，展开面板的进度条和百分比、配额提醒也显示剩余的比例，余额和扣费等金额不变。
- **低峰时间**：最多添加 6 个低峰时段，并可为低峰时段单独选择颜色。处于低峰时段时，这一列的配额环与进度条改用低峰颜色。

## WSL 中的用量

在"设置 › 配额与统计"中打开"同步 WSL 中的用量统计"后，BrimDeck 会读取已安装的 WSL 子系统中 Claude Code 与 Codex CLI 的会话记录，计入 Claude 与 Codex 的 Token 用量与费用。目前只有 Claude 与 Codex 支持 WSL。

- 读取范围为每个子系统中 root 用户和 `/home` 下各用户的记录；Docker Desktop 与 Rancher Desktop 自带的子系统不会读取。
- 读取时会启动尚未运行的 WSL 子系统，因此这个选项默认关闭。

## Claude 与 Grok Bot 的"联网获取额度"

配额来源为 Claude 或 Grok Bot 的行可以展开，每一行可以单独开关"联网获取额度"（默认开启）：

- **开启**：使用该应用在本机的登录，联网读取账户额度。读取失败时，显示最近一次成功读取的额度或桌面版在本机记录的额度，并标明时间。Claude 重启 BrimDeck 后仍会显示同一账户上次读取的额度。Claude 账户同时属于多个组织（例如个人 Pro 与 Team）时，同一行显示各组织的额度，个人订阅在前，点击该列翻到下一页。Claude 的账户接口提示请求频繁（429）时，按接口给出的等待时间暂停请求（超过 7 天的当作无效），没有给出时依次等待 2、4、8、16 分钟，最长 30 分钟；暂停期间不再每分钟请求，手动刷新也不会绕过，状态里会写明下次请求的时间。
- **关闭**：不读取登录，也不联网，只显示桌面版在本机记录的最新额度。这些数据标为旧数据，不触发额度提醒。

## Grok Bot

Grok Bot 一行只显示 Grok Bot 桌面版登录账户的每周额度：已用比例、重置时间与套餐名称，不显示 Cursor 的其他配额与按量计费金额。

- **联网**：使用 Grok Bot 桌面版自己的登录读取，不使用 Cursor 编辑器的登录，也不续期登录。登录失效时，请打开 Grok Bot 桌面版。
- **不联网**：显示 Grok Bot 桌面版在本机缓存的每周额度，时间为桌面版上次读取的时间。
- 账户使用企业共享额度时，不显示已用比例。

## DeepSeek Harness

DeepSeek Harness 桌面版与本地 Web 版（`npx @deepseek-ai/dsh web`）共用同一个数据目录，两者任选其一登录即可。

- **配额**：在 DeepSeek Harness 中登录了 DeepSeek 账户时，显示该账户的余额、赠送余额与累计消费金额，与 DeepSeek 开放平台网页显示的数字一致，均按整个账户计算。未登录账户时，使用在 DeepSeek Harness 模型页面保存的 API 密钥，只能显示余额与赠送余额。登录失效时，请在 DeepSeek Harness 中重新登录。
- **Token 用量与费用**：读取本机的 DeepSeek Harness 会话记录，包含桌面版与本地 Web 版。费用按模型单价估算；单价表中没有的模型不计算费用，可以在设置中手动添加单价。

## Kimi Code

Kimi Code CLI、桌面版与 VS Code 扩展共用同一个数据目录与登录，任选其一登录即可。

- **配额**：显示 Kimi Code 登录账户的会员套餐额度，包括 5 小时额度，以及每周或每月额度（视套餐而定）；名称旁显示会员等级。购买了加油包时，另外显示加油包余额；设置了每月消费上限时，显示本月已用金额。账户未订阅 Kimi Code 套餐时，状态显示“未订阅 Kimi Code 套餐”。
- **登录**：使用 Kimi Code 自己的登录，不续期登录。登录过期时不发送请求，显示最近一次读取的额度并注明读取时间，使用 Kimi Code 后自动更新。
- **国内版与海外版**：按 Kimi Code 登录时选择的站点区分国内版（kimi.com，接口 api.kimi.com）与海外版（kimi.ai，接口 api.kimi.ai），读取对应的登录与接口；来源显示为“Kimi Code · kimi.com 账户”或“Kimi Code · kimi.ai 账户”，会员等级按该站点返回的名称显示。两个站点的额度分别记录，切换站点或账户后不显示另一方的额度。
- **服务地址**：按 Kimi Code 的设置读取。设置了 `KIMI_CODE_BASE_URL` 时使用该地址，须为 HTTPS；同时支持 `KIMI_CODE_OAUTH_HOST` 与 `KIMI_CODE_REGION_MARKER`，与 Kimi Code 的用法一致。
- **Token 用量与费用**：读取本机的 Kimi Code 会话记录，包含 CLI、桌面版与 VS Code 扩展，以及其中的子代理。K3（`kimi-k3`）、K3.1 Pro（`kimi-k3.1-pro`）与 K3.1 Pro Highspeed（`kimi-k3.1-pro-highspeed`）按 OpenRouter 上 Moonshot AI 官方渠道的单价估算费用（包括缓存读取，渠道提供时也包括缓存写入），不采用其他服务商的价格；该价格随单价表每天更新并保存在本机，更新失败时沿用上次保存的价格，从未获取到时只统计 Token。`k3-256k` 单独列出，按 K3 的单价计算。`kimi-for-coding` 与 `kimi-for-coding-highspeed` 是 Kimi Code 会随时间指向不同模型的别名，只统计 Token，不计算费用。手动添加的单价优先，包括为 `k3-256k` 单独添加的单价。

## Pi

支持读取 Pi Coding Agent（`~/.pi/agent`）在本机的会话用量与费用统计，无需安装任何 Pi 插件。

- **统计应用**：在“统计应用”中选择 Pi，可与现有配额来源搭配使用；“配额来源”中不提供 Pi。不读取 Pi 登录、不查询第三方订阅额度，也不提供 Pi 多账号切换。
- **Token 用量与费用**：读取 `~/.pi/agent/sessions` 中的会话记录，统计各模型的今日与七日 Token 用量；费用采用 Pi 记录的数值，不代表订阅账单。
- **会话路径**：`PI_CODING_AGENT_SESSION_DIR` 优先；未设置时读取 `PI_CODING_AGENT_DIR` 下的 `sessions`，两者均未设置时使用默认目录。Pi 专用解析支持 `~`、`~/...`、相对路径和本地 `file://` URL；Windows 下也支持 `~\...` 与 Git Bash/MSYS/Cygwin/WSL 的盘符路径。相对路径以 BrimDeck 进程的工作目录为基准，不保证与另一个终端中 Pi 的工作目录相同。只读取所选目录，不自动扫描所有 Pi 配置。

## OpenCode

OpenCode CLI 与桌面版共用同一个数据目录（`%USERPROFILE%\.local\share\opencode`），登录与会话记录都保存在其中的 `opencode.db`，任选其一登录即可。OpenCode 1.x 与 2.x 也使用这个目录。

- **配额**：显示 OpenCode 登录 OpenCode Console 时所选工作区的 OpenCode Go 额度，包括 5 小时、每周与每月额度的已用比例和重置时间。OpenCode 只返回比例，不返回套餐档位，因此不区分 Go 与 Go Plus。工作区未订阅 OpenCode Go 时，状态显示"未订阅 Go"；达到用量上限时，状态中会提示。
- **登录**：使用 OpenCode 自己的登录，不续期登录。登录过期时不发送请求，使用一次 OpenCode 即可续期。OpenCode 中没有 Console 登录、只保存了 API 密钥时，使用该密钥。
- **数据目录**：设置了 `XDG_DATA_HOME`（须为绝对路径）时，读取其中的 `opencode` 目录；设置了 `OPENCODE_DB` 时，读取该数据库，与 OpenCode 的用法一致。
- **Token 用量与费用**：读取本机 OpenCode 数据库中的每次模型请求，包含 CLI 与桌面版，以及 OpenCode 1.x 留下的记录。费用使用 OpenCode 记录的金额，即按 OpenCode 标价计算的金额：免费模型为 0；OpenCode Go 的模型同样按标价计算，表示这次请求相当于多少钱，不是从账户余额中扣除的金额。失败的请求没有 Token，不计入。OpenCode 为会话生成标题的请求没有单独的记录，不计入。
- OpenCode Console 的按量付费余额目前不显示。

## Command Code

Command Code CLI 与桌面版共用同一个数据目录（`%USERPROFILE%\.commandcode`），登录与会话记录都保存在其中，任选其一登录即可。

- **配额**：显示 Command Code 登录账户的套餐额度，包括 5 小时、每周与每月额度的已用比例和重置时间。每月额度按本账期计算，在账期结束时重置。悬停时显示已用金额与上限（美元）。购买或获赠的额外积分不受这些额度限制，有余额时另外显示。名称旁显示套餐名称（例如 Go、Pro）。用完 5 小时或每周额度时，状态中会提示。
- **登录**：使用 Command Code 保存的登录密钥（`auth.json`）。该密钥没有有效期，不需要续期。Command Code 拒绝该密钥时（例如已退出登录或删除了密钥），状态显示“登录已失效”，在 Command Code 中重新登录即可。
- **Token 用量与费用**：读取本机 Command Code 的会话记录（`projects` 目录），包含 CLI 与桌面版。费用使用 Command Code 记录的金额，即按 Command Code 标价计算的金额。模型明细中的单价取自 Command Code 在本机缓存的模型目录（`cache\model-catalog`），即 Command Code 计算这些金额所用的单价。同一条消息出现在多个会话中（例如分叉的会话）时只计一次。Command Code 为会话生成标题等不写入会话记录的请求不计入，因此统计可能略少于 Command Code 账户中的用量。

## WorkBuddy

支持 WorkBuddy 国内版（www.workbuddy.cn）。WorkBuddy 桌面版把自己的登录加密保存，BrimDeck 不读取它，而是通过 WorkBuddy 官网单独登录一次。

- **配额**：显示 WorkBuddy 账户的积分余额，按套餐积分、套餐赠送积分、购买积分与奖励积分分别显示，没有的种类不显示。名称旁显示套餐名称（例如体验版）。时间与 WorkBuddy 客户端的显示方式一致：套餐积分与套餐赠送积分显示本周期结束后的刷新时间；购买积分与奖励积分不按周期刷新，显示尚未过期的包中最早的过期时间，已过期的包不计入。国内版 WorkBuddy 与 CodeBuddy 同一账号的积分共享，所以这里的余额也包含在 CodeBuddy 中的消耗。
- **登录**：在设置中展开 WorkBuddy 行，点击“在浏览器中登录”，在打开的 WorkBuddy 官网页面登录并授权即可，需在 10 分钟内完成。BrimDeck 获得自己的登录凭据，加密保存在本机，只发往 www.workbuddy.cn；过期时自动续期，续期失败时状态显示“登录已失效”，重新登录即可。点击“退出登录”删除本机保存的凭据。只支持个人账号。
- **Token 用量与费用**：读取本机 WorkBuddy 国内版桌面应用的会话记录（`%USERPROFILE%\.workbuddy\projects`），统计每次模型回复的输入、缓存读取、缓存写入与输出 Token。费用按模型公开单价估算，不是 WorkBuddy 扣除的积分。WorkBuddy 记录的模型名带有通道后缀（例如 `hy4-free`），统计中按原样显示，查单价时使用记录中的模型名称（例如 Hy4，对应单价表中的 `tencent/hy4`）；单价表中没有的模型只统计 Token。同一次回复出现在多个会话中时只计一次。
- WorkBuddy 的套餐积分只能在 WorkBuddy 与 CodeBuddy 中使用，没有可在其他工具中消耗积分的 API 密钥。
- **国际版**：WorkBuddy 国际版（www.workbuddy.ai，桌面应用名为 WorkBuddy AI）是独立的账号体系，在“配额来源”与“统计应用”中单独选择 WorkBuddy AI，与国内版分别登录、分别显示。读取方式与国内版相同，会话记录读取 `%USERPROFILE%\.workbuddy-ai\projects`；凭据只发往 www.workbuddy.ai。积分归类与 WorkBuddy AI 客户端一致，例如体验类积分在国际版中计为奖励积分。默认颜色与国内版相同。

## Xiaomi MiMo 桌面订阅

MiMo Code CLI 与 Xiaomi MiMo 桌面版共用同一个数据库（`%USERPROFILE%\.local\share\mimocode\mimocode.db`），两者的对话记录一起统计。

- **配额**：显示 Xiaomi MiMo 桌面版会员的每周额度的已用比例与重置时间，名称旁显示会员档位（入门、进阶、高阶或尊享）。桌面版会员与 Token Plan 是两套独立的订阅，这里不显示 Token Plan 的额度。
- **登录**：桌面版会员没有 API 密钥，需要在 BrimDeck 中单独登录小米账号。在设置中展开 Xiaomi MiMo 桌面订阅行，点击“登录”，BrimDeck 会用默认浏览器（不是 Chrome、Edge 等 Chromium 浏览器时改用 Edge）在一个独立的新窗口中打开小米账号的官方登录页，可用密码、手机号、微信、支付宝等方式登录，需在 10 分钟内完成。登录后 BrimDeck 关闭这个窗口，只保存查询额度所需的登录信息，加密保存在本机；你平时使用的浏览器与它的登录状态不受影响。登录过期时自动续期，续期失败时状态显示“登录已失效”，重新登录即可。点击“退出登录”删除本机保存的登录信息。
- **Token 用量与费用**：读取本机 MiMo 数据库中每次模型回复的输入、缓存读取、缓存写入与输出 Token。桌面版会员的请求在记录中金额为 0，按模型公开单价估算；使用开放平台密钥的请求使用 MiMo 记录的金额。

## Xiaomi MiMo

自动读取 Xiaomi MiMo 桌面版与 MiMo Code CLI 已关联的开放平台 API 密钥或 Token Plan（与数据库同目录的 `auth.json`），不需要在 BrimDeck 中填写。

- 关联了 Token Plan 时显示套餐名称与 Credits 额度；关联了按量计费 API 密钥时显示账户余额、充值余额、赠送余额与本月扣费。两者都关联时，在同一列中一起显示。
- 每次刷新都重新读取这个文件。桌面版关联和 CLI 登录都会改写它，例如在 CLI 中重新登录后，桌面版关联的 Token Plan 会从文件中消失，这里也随之不再显示。
- 用量统计使用 Xiaomi MiMo 的本机记录。

## 手动填写的来源

在"配额来源"中选择智谱 GLM、Z.ai GLM、Xiaomi MiMo API、Xiaomi MiMo Token Plan、DeepSeek、Kimi Code API、Kimi 开放平台、OpenCode Go、Command Code API、OpenRouter、New API、Sub2API 或自定义后，该行会展开，填写所需内容即可。

- **密钥**：使用 Windows 的加密功能保存在本机，不写入设置文件。删除该列或更换配额来源时，密钥一并删除。
- **站点地址**：填写站点首页地址即可。末尾的 `/v1`，以及直接粘贴的控制台页面路径，会自动去掉。请求会带上 API 密钥，所以站点地址须使用 HTTPS；HTTP 仅限本机地址（`localhost`、`127.0.0.1`、`[::1]`）。
- **允许 HTTP**（New API、Sub2API、自定义）：默认关闭。站点自建在 NAS 上（例如 `http://192.168.x.x`）或通过 Tailscale 访问时，可以为这一来源打开。打开后，此来源的请求（包括自定义脚本发出的请求）可以使用 HTTP 访问任意地址，API 密钥会以明文发送，只在可信的网络中使用。未打开时，已保存的 HTTP 地址不会读取，状态显示“需要 HTTPS”。
- **测试连接**：立即读取一次，显示读取结果与耗时。
- **读取失败时**：保留最近一次成功读取的数据，名称的悬停提示中会说明失败原因。

### 智谱 GLM 与 Z.ai GLM

填写 GLM Coding Plan 的 API 密钥。显示 5 小时与每周额度，以及工具调用次数；积分制套餐显示已用积分与总积分。不返回额度的团队套餐暂不支持。

### Xiaomi MiMo API

填写在小米 MiMo 开放平台（platform.xiaomimimo.com）创建的按量计费 API 密钥（`sk-` 开头）。显示账户余额、充值余额、赠送余额（有冻结金额时也显示），以及本月扣费。本月按 UTC 自然月计算。可以设置目标余额。

Xiaomi MiMo 桌面版会员不提供 API 密钥，它的额度请使用"Xiaomi MiMo 桌面订阅"。

### Xiaomi MiMo Token Plan

填写 Token Plan 页面上的专属 API 密钥（`tp-` 或 `ttp-` 开头）。显示套餐名称（例如 Lite）与 Credits 额度的已用量，以及当前周期的结束时间。BrimDeck 依次在中国、新加坡、欧洲集群查询，直到找到密钥所属的集群。Token Plan 额度由使用这个套餐的所有工具共用。

### DeepSeek

填写在 DeepSeek 开放平台创建的 API 密钥。显示账户余额与其中的赠送余额，金额按账户的货币（人民币或美元）显示。余额不足以调用 API 时，状态中会提示。

### Kimi Code API

填写在 Kimi Code 控制台创建的 API 密钥（`sk-kimi-` 开头），无需在本机登录 Kimi Code。显示的额度与自动读取的 Kimi Code 相同：5 小时与每月额度，旧版套餐另有每周额度，购买了加油包时显示加油包余额；名称旁显示会员等级。国内版（kimi.com）与海外版（kimi.ai）的密钥都可以使用：先向 api.kimi.com 查询，密钥被拒绝时改向 api.kimi.ai 查询，来源按账户所属站点显示为“Kimi Code API · kimi.com 账户”或“Kimi Code API · kimi.ai 账户”。此密钥不能用于 Kimi 开放平台，因此不显示开放平台余额，也不统计 Token 用量；开放平台余额请使用“Kimi 开放平台”。

### Kimi 开放平台

填写在 Kimi 开放平台（platform.kimi.com 或 platform.kimi.ai）创建的 API 密钥。开放平台按量计费，与 Kimi 会员和 Kimi Code 的套餐额度分开计算。显示账户余额（现金余额与代金券余额之和），以及现金余额与代金券余额；欠费时现金余额为负数。账户余额不足、无法调用时，状态中会注明。可以设置目标余额。国内版与海外版的密钥都可以使用：先向 api.moonshot.cn 查询，密钥被拒绝时改向 api.moonshot.ai 查询；国内版以人民币显示，海外版以美元显示，来源显示为“Kimi 开放平台 · kimi.com 账户”或“Kimi 开放平台 · kimi.ai 账户”。Kimi Code 控制台的密钥（`sk-kimi-` 开头）不能在这里使用，请选择 Kimi Code API。

### OpenCode Go

填写在 OpenCode Console 创建的 API 密钥（`oc_sk_` 开头），无需在本机安装或登录 OpenCode。显示该密钥所属工作区的 OpenCode Go 额度，与自动读取的 OpenCode 相同：5 小时、每周与每月额度。工作区未订阅 OpenCode Go 时，状态中会说明。不统计 Token 用量。

### Command Code API

填写在 Command Code Studio 的 API 密钥页面（https://commandcode.ai/settings/keys）创建的密钥，无需在本机安装或登录 Command Code。显示的额度与自动读取的 Command Code 相同：5 小时、每周与每月额度，以及额外积分；名称旁显示套餐名称。同一个密钥也可以通过 Command Code 的 Provider API 在其他编程工具中使用，这些请求消耗同一账户的额度。不统计 Token 用量。

### OpenRouter

填写在 OpenRouter 创建的 API 密钥（`sk-or-v1-` 开头）。显示账户余额（已购买的额度减去已用额度），以及这个密钥今日与本月的扣费，金额以美元显示。今日与本月按 UTC 时间计算，在北京时间 08:00 重置。密钥设置了额度上限时，另外显示密钥余额与重置时间；账户管理员设置了预算时，显示最先限制请求的预算余额。不统计 Token 用量。

### New API

填写站点地址与 API 密钥。按站点和密钥的情况，显示账户余额或密钥余额、今日扣费，以及无上限密钥的累计消费。金额按站点设置的货币与汇率换算。

### Sub2API

填写站点地址与 API 密钥。钱包模式显示账户余额与今日扣费，订阅模式显示每日、每周、每月额度，限额密钥显示各时间段额度与密钥余额。名称旁的标签显示倍率或套餐名。

选错 New API 与 Sub2API 时，读取结果会提示改用另一种。

### 自定义脚本

选择"自定义"后，代码框会预先填入可直接运行的模板。脚本需定义 `fetchUsage(ctx)`，返回要显示的配额数组：

```javascript
async function fetchUsage(ctx) {
  const res = await ctx.http.get(ctx.site + "/api/usage", {
    headers: { Authorization: "Bearer " + ctx.secrets.key }
  });
  if (res.status !== 200) throw new Error("读取失败：HTTP " + res.status);
  const data = res.json();
  return [
    { title: "本月", percent: data.used / data.limit * 100, value: "$" + data.used.toFixed(2), suffix: "已用", resetAt: data.resetText }
  ];
}
```

`ctx` 提供：

| 成员 | 说明 |
| --- | --- |
| `ctx.site`、`ctx.secrets.key` | 填写的站点地址与 API 密钥，未填写时为空字符串 |
| `ctx.http.get(url, options)`、`ctx.http.post(url, options)` | 发送请求。`options` 可包含 `headers`、`timeout`（毫秒）与 `body`（仅 POST）。返回 `{ status, headers, text(), json() }`。地址须使用 HTTPS，规则与站点地址相同 |
| `ctx.log(text)` | 写一行日志，在"测试连接"中显示 |
| `ctx.language`、`ctx.now` | 界面语言（`"zh-CN"` 或 `"en-US"`）与本次读取的时间 |

返回数组中的每一项是一条配额，按顺序显示：

| 字段 | 说明 |
| --- | --- |
| `title` | 必填。配额名称 |
| `percent` | 选填，0–100。已用比例，决定进度条长度与颜色 |
| `value`、`suffix` | 选填。大号数字及其右侧的小字，例如 `"$12.30"` 与 `"已用"` |
| `note` | 选填。与名称同一行，靠右显示 |
| `resetAt` | 选填。与数字同一行，靠右显示，直接显示原文，例如 `"2 时 13 分后"` |

不符合要求的项会被跳过，并在读取结果中说明原因。脚本抛出的错误会原样显示。

脚本在沙盒中运行，无法访问文件、进程或 .NET 对象。每次读取最长 10 秒，最多发送 16 个请求，请求与响应各不超过 1 MB。
