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
| 智谱 GLM、Z.ai GLM | ✓ | | 在设置中填写 API 密钥 |
| DeepSeek | ✓ | | 在设置中填写 API 密钥 |
| Kimi Code API | ✓ | | 在设置中填写 Kimi Code API 密钥 |
| OpenCode Go | ✓ | | 在设置中填写 OpenCode Console API 密钥 |
| OpenRouter | ✓ | | 在设置中填写 OpenRouter API 密钥 |
| New API、Sub2API 中转站 | ✓ | | 在设置中填写站点地址和密钥 |
| 自定义 | ✓ | | 编写一段 JavaScript 脚本 |

手动填写的来源与 Grok Bot 没有本地用量记录，"今日"和"七日"可以另选一个工具统计。

## 费用估算

记录本身带有金额时（例如 Cursor 与 OpenCode），直接使用该金额。其他记录按 OpenRouter 模型目录中的单价估算。

目录中的单价通常取自最便宜的服务商，可能与模型厂商的官方价格不同。例如 GLM-5.3 在目录中的输入与输出单价为每百万 Token 0.07 与 1.4 美元，Z.AI 官方为 1.4 与 4.4 美元。因此，对记录中用到的每个模型，BrimDeck 会查询 OpenRouter 上该模型厂商自己的渠道，并改用该渠道的单价；厂商没有渠道的模型，沿用目录单价。厂商渠道的单价每个模型每天查询一次，并保存在本机；查询失败时沿用上次保存的单价。手动添加的单价优先。

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

## 手动填写的来源

在"配额来源"中选择智谱 GLM、Z.ai GLM、DeepSeek、Kimi Code API、OpenCode Go、OpenRouter、New API、Sub2API 或自定义后，该行会展开，填写所需内容即可。

- **密钥**：使用 Windows 的加密功能保存在本机，不写入设置文件。删除该列或更换配额来源时，密钥一并删除。
- **站点地址**：填写站点首页地址即可。末尾的 `/v1`，以及直接粘贴的控制台页面路径，会自动去掉。请求会带上 API 密钥，所以站点地址须使用 HTTPS；HTTP 仅限本机地址（`localhost`、`127.0.0.1`、`[::1]`）。
- **允许 HTTP**（New API、Sub2API、自定义）：默认关闭。站点自建在 NAS 上（例如 `http://192.168.x.x`）或通过 Tailscale 访问时，可以为这一来源打开。打开后，此来源的请求（包括自定义脚本发出的请求）可以使用 HTTP 访问任意地址，API 密钥会以明文发送，只在可信的网络中使用。未打开时，已保存的 HTTP 地址不会读取，状态显示“需要 HTTPS”。
- **测试连接**：立即读取一次，显示读取结果与耗时。
- **读取失败时**：保留最近一次成功读取的数据，名称的悬停提示中会说明失败原因。

### 智谱 GLM 与 Z.ai GLM

填写 GLM Coding Plan 的 API 密钥。显示 5 小时与每周额度，以及工具调用次数；积分制套餐显示已用积分与总积分。不返回额度的团队套餐暂不支持。

### DeepSeek

填写在 DeepSeek 开放平台创建的 API 密钥。显示账户余额与其中的赠送余额，金额按账户的货币（人民币或美元）显示。余额不足以调用 API 时，状态中会提示。

### Kimi Code API

填写在 Kimi Code 控制台创建的 API 密钥（`sk-kimi-` 开头），无需在本机登录 Kimi Code。显示的额度与自动读取的 Kimi Code 相同：5 小时与每月额度，旧版套餐另有每周额度，购买了加油包时显示加油包余额；名称旁显示会员等级。国内版（kimi.com）与海外版（kimi.ai）的密钥都可以使用：先向 api.kimi.com 查询，密钥被拒绝时改向 api.kimi.ai 查询，来源按账户所属站点显示为“Kimi Code API · kimi.com 账户”或“Kimi Code API · kimi.ai 账户”。此密钥不能用于 Moonshot 开放平台，因此不显示开放平台余额，也不统计 Token 用量。

### OpenCode Go

填写在 OpenCode Console 创建的 API 密钥（`oc_sk_` 开头），无需在本机安装或登录 OpenCode。显示该密钥所属工作区的 OpenCode Go 额度，与自动读取的 OpenCode 相同：5 小时、每周与每月额度。工作区未订阅 OpenCode Go 时，状态中会说明。不统计 Token 用量。

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
