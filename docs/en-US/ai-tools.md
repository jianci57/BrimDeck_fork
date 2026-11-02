# Supported tools

Each column of the AI usage page can be set up separately in Settings › Quotas and usage:

- **Quota source**: the plan, quota usage and reset countdowns shown in the column.
- **Usage app**: the tokens and estimated cost for Today and Week (the last 7 days) at the bottom of the column, and its model details.
- **Display name and colors**: a custom name, and the theme, warning and critical colors.

The same tool can appear in several columns. It is read only once, and totals are never counted twice.

| Tool | Quota | Tokens and cost | What you need |
| --- | :---: | :---: | --- |
| Claude | ✓ | ✓ | Sign in to Claude Code or the Claude desktop app on this PC. Usage comes from Claude Code |
| Codex | ✓ | ✓ | Sign in to Codex on this PC |
| Cursor | ✓ | ✓ | Sign in to Cursor on this PC |
| Grok Bot | ✓ | | Sign in to the Grok Bot desktop app on this PC |
| Antigravity | ✓ | ✓ | The Antigravity desktop app must be running for its quota to be read |
| ZCode | ✓ | ✓ | Sign in to ZCode on this PC |
| DeepSeek Harness | ✓ | ✓ | Sign in to a DeepSeek account in the DeepSeek Harness desktop app or local web app, or save an API key there |
| Kimi Code | ✓ | ✓ | Sign in to Kimi Code (CLI, desktop app or VS Code extension) on this PC |
| Pi | | ✓ | Use Pi Coding Agent on this PC (reads session token and cost statistics) |
| OpenCode | ✓ | ✓ | Quota: sign in to OpenCode Console in OpenCode (CLI or desktop app) on this PC, with an OpenCode Go plan on the workspace. Usage only needs OpenCode to be used on this PC |
| Zhipu GLM, Z.ai GLM | ✓ | | Enter an API key in settings |
| DeepSeek | ✓ | | Enter an API key in settings |
| Kimi Code API | ✓ | | Enter a Kimi Code API key in settings |
| OpenCode Go | ✓ | | Enter an OpenCode Console API key in settings |
| OpenRouter | ✓ | | Enter an OpenRouter API key in settings |
| New API, Sub2API relay sites | ✓ | | Enter the site address and key in settings |
| Custom | ✓ | | Write a short JavaScript script |

Sources you enter by hand and Grok Bot have no local usage records; choose another tool as the usage app for Today and Week.

## Cost estimates

When a record carries its own amount (Cursor and OpenCode, for example), that amount is used. Other records are costed at the prices in the OpenRouter model list.

A price in that list usually comes from the cheapest provider and can differ from the model maker's list price. For example, GLM-5.3 is listed at 0.07 and 1.4 USD per million input and output tokens, while Z.AI charges 1.4 and 4.4. So for each model your records use, BrimDeck looks up the model maker's own endpoint on OpenRouter and uses its prices instead; a model whose maker has no endpoint there keeps the list price. Each model's endpoint prices are looked up once a day and saved on this PC; when a lookup fails, the prices saved before are used. Prices added by hand take precedence.

## Usage in WSL

When "Include usage from WSL" is turned on in Settings › Quotas and usage, BrimDeck reads the Claude Code and Codex CLI session records inside installed WSL distributions and counts them in the tokens and cost of Claude and Codex. Only Claude and Codex support WSL at present.

- The records of the root user and of every user under `/home` in each distribution are read. The distributions that Docker Desktop and Rancher Desktop add are not read.
- Reading starts a distribution that is not running, so this option is off by default.

## Fetch quota online for Claude and Grok Bot

Rows whose quota source is Claude or Grok Bot can be expanded, and each row has its own "Fetch quota online" switch (on by default):

- **On**: the app's sign-in on this PC is used to read the account quota online. If a read fails, the last successful reading or the quota the desktop app recorded on this PC is shown, with its time. For Claude it is still shown after restarting BrimDeck, for the same account. When a Claude account belongs to several organizations (a personal Pro and a Team, for example), the row shows the quotas of each, the personal subscription first; click the column for the next page. When Claude's account API answers that there are too many requests (429), requests pause for the wait it names (a wait over 7 days is taken as invalid), or for 2, 4, 8 and 16 minutes in turn (30 at most) when it names none. No request goes out each minute during the pause, a manual refresh does not go round it, and the status gives the time of the next request.
- **Off**: no sign-in is read and nothing is sent; only the latest quota the desktop app recorded on this PC is shown. This data is marked as old and does not trigger quota alerts.

## Grok Bot

A Grok Bot row shows only the weekly allowance of the account signed in to the Grok Bot desktop app: the share used, the reset time and the plan name. Other Cursor quotas and on-demand spending are not shown.

- **Online**: read with the Grok Bot desktop app's own sign-in, not the Cursor editor's, and the sign-in is never renewed. If the sign-in has expired, open the Grok Bot desktop app.
- **Offline**: shows the weekly allowance the Grok Bot desktop app cached on this PC, timed when the desktop app last read it.
- When the account uses a pooled enterprise allowance, the share used is not shown.

## DeepSeek Harness

The DeepSeek Harness desktop app and local web app (`npx @deepseek-ai/dsh web`) share one data folder, so signing in to either is enough.

- **Quota**: when a DeepSeek account is signed in to DeepSeek Harness, shows that account's balance, granted balance and total cost, the same figures as the DeepSeek platform's web console, all for the whole account. Without a signed-in account, the API key saved on the DeepSeek Harness Models page is used, and only the balance and the granted balance can be shown. If the sign-in expires, sign in again in DeepSeek Harness.
- **Tokens and cost**: read from the DeepSeek Harness session records on this PC, covering the desktop app and the local web app. Costs are estimated from model prices; models missing from the price list are not priced, and you can add their prices in settings.

## Kimi Code

Kimi Code CLI, the desktop app and the VS Code extension share one data folder and sign-in, so signing in to any of them is enough.

- **Quota**: shows the membership plan quota of the account signed in to Kimi Code: the 5-hour limit, and the weekly or monthly limit, depending on the plan; the membership level appears beside the name. With extra usage purchased, its balance is shown too, and with a monthly spending cap set, the amount used this month. When the account has no Kimi Code plan, the status reads "No Kimi Code plan".
- **Sign-in**: uses Kimi Code's own sign-in and never renews it. While the sign-in has expired, no request is sent and the last quota read is shown with its time; it updates automatically after Kimi Code is used.
- **Mainland China and international**: follows the site chosen when signing in to Kimi Code, kimi.com (API api.kimi.com) or kimi.ai (API api.kimi.ai), and reads that sign-in and API. The source reads "Kimi Code · kimi.com account" or "Kimi Code · kimi.ai account", and the membership level is shown as that site names it. Each site's quota is kept separately, so after switching site or account the other one's quota is never shown.
- **Service address**: follows Kimi Code's settings. When `KIMI_CODE_BASE_URL` is set, that address is used; it must use HTTPS. `KIMI_CODE_OAUTH_HOST` and `KIMI_CODE_REGION_MARKER` are honored as Kimi Code uses them.
- **Tokens and cost**: read from the Kimi Code session records on this PC, covering the CLI, the desktop app and the VS Code extension, including their subagents. K3 (`kimi-k3`), K3.1 Pro (`kimi-k3.1-pro`) and K3.1 Pro Highspeed (`kimi-k3.1-pro-highspeed`) are costed at the prices of the official Moonshot AI endpoint on OpenRouter (including cache reads, and cache writes when the endpoint lists them), never another provider's price. These prices are updated daily with the price list and saved on this PC; when an update fails the last saved prices are used, and until they have been read once these models show tokens only. `k3-256k` is listed on its own and costed at K3's price. `kimi-for-coding` and `kimi-for-coding-highspeed` are aliases that Kimi Code points at different models over time, so they show tokens only, with no cost. Prices added by hand take precedence, including one added for `k3-256k` itself.

## Pi

Reads local session usage and cost statistics from Pi Coding Agent (`~/.pi/agent`) without installing any Pi extension.

- **Usage app**: select Pi under "Usage app" and pair it with an existing quota source. Pi is not offered under "Quota source". No Pi sign-in, third-party subscription quota or Pi account switching is read or provided.
- **Tokens and cost**: reads session records from `~/.pi/agent/sessions` and counts token usage and Pi-recorded costs for Today and Week, not subscription charges.
- **Session path**: `PI_CODING_AGENT_SESSION_DIR` takes precedence; otherwise reads `sessions` under `PI_CODING_AGENT_DIR`, or the default directory when neither is set. Pi-specific resolution supports `~`, `~/...`, relative paths and local `file://` URLs; Windows also supports `~\...` and Git Bash/MSYS/Cygwin/WSL drive paths. Relative paths use the BrimDeck process working directory, which may differ from Pi running in another terminal. Only the selected directory is read; other Pi profiles are not automatically scanned.
## OpenCode

The OpenCode CLI and desktop app share one data folder (`%USERPROFILE%\.local\share\opencode`), and keep the sign-in and session records in `opencode.db` there, so signing in to either is enough. OpenCode 1.x and 2.x use this folder too.

- **Quota**: shows the OpenCode Go allowance of the workspace chosen when OpenCode signed in to OpenCode Console: the share used and reset time of the 5-hour, weekly and monthly limits. OpenCode returns only the shares, not the plan tier, so Go and Go Plus are not told apart. When the workspace has no OpenCode Go plan, the status reads "No Go plan"; when a limit is reached, the status says so.
- **Sign-in**: uses OpenCode's own sign-in and never renews it. While the sign-in has expired, no request is sent; using OpenCode once renews it. When OpenCode has no Console sign-in but has an API key saved, that key is used.
- **Data folder**: when `XDG_DATA_HOME` is set to an absolute path, the `opencode` folder in it is read; when `OPENCODE_DB` is set, that database is read, as OpenCode does.
- **Tokens and cost**: read from every model request in the OpenCode database on this PC, covering the CLI and the desktop app, and records left by OpenCode 1.x. The cost is the amount OpenCode recorded, at OpenCode's list prices: free models cost 0, and OpenCode Go models are costed at list price too, showing what the request is worth rather than what was taken from the account balance. Failed requests have no tokens and are not counted. The request OpenCode makes to title a session has no record of its own and is not counted.
- The pay-as-you-go balance of OpenCode Console is not shown yet.

## Sources entered by hand

Choose Zhipu GLM, Z.ai GLM, DeepSeek, Kimi Code API, OpenCode Go, OpenRouter, New API, Sub2API or Custom as the quota source, and the row expands so you can fill in what it needs.

- **Key**: stored on this PC, encrypted by Windows, and never written to the settings file. It is deleted when you remove the column or change its quota source.
- **Site address**: the home page address is enough. A trailing `/v1`, or the path of a console page you paste, is removed automatically. Requests carry the API key, so the address must use HTTPS; HTTP is only for this PC (`localhost`, `127.0.0.1`, `[::1]`).
- **Allow HTTP** (New API, Sub2API, Custom): off by default. Turn it on for a source whose site runs on a NAS (such as `http://192.168.x.x`) or is reached through Tailscale. When on, the source's requests, including those of a custom script, may use HTTP to any address, and the API key is sent in plain text, so use it only on a trusted network. When off, a saved HTTP address is not read and the status says "HTTPS required".
- **Test connection**: reads the source once and shows the result and how long it took.
- **If a read fails**: the last successful reading stays on screen, and the tooltip on the name explains why.

### Zhipu GLM and Z.ai GLM

Enter the API key of your GLM Coding Plan. Shows the 5-hour and weekly limits and tool call counts; credit-based plans show credits used and the total. Team plans whose API returns no limits are not supported yet.

### DeepSeek

Enter an API key created on the DeepSeek platform. Shows the account balance and the granted part of it, in the account's currency (CNY or USD). When the balance is too low to call the API, the status says so.

### Kimi Code API

Enter an API key created in the Kimi Code console (starting with `sk-kimi-`); Kimi Code does not need to be signed in on this PC. Shows the same quota as the automatic Kimi Code source: the 5-hour and monthly limits, the weekly limit on legacy plans, and the extra-usage balance when purchased; the membership level appears beside the name. Keys for both mainland China (kimi.com) and international (kimi.ai) accounts work: api.kimi.com is asked first, and api.kimi.ai when it rejects the key. The source reads "Kimi Code API · kimi.com account" or "Kimi Code API · kimi.ai account", by the account's site. The key does not work on the Moonshot open platform, so no open-platform balance is shown, and no token usage is counted.

### OpenCode Go

Enter an API key created in OpenCode Console (starting with `oc_sk_`); OpenCode does not need to be installed or signed in on this PC. Shows the OpenCode Go allowance of the key's workspace, the same as the automatic OpenCode source: the 5-hour, weekly and monthly limits. When the workspace has no OpenCode Go plan, the status says so. No token usage is counted.

### OpenRouter

Enter an API key created on OpenRouter (starting with `sk-or-v1-`). Shows the account balance (credits purchased minus credits used) and what this key has been charged today and this month, in US dollars. Today and this month follow UTC, so they reset at midnight UTC. When the key has a credit limit, its remaining amount and reset time are shown too; when an account admin has set a budget, the budget that limits requests first is shown instead. No token usage is counted.

### New API

Enter the site address and API key. Depending on the site and the key, shows the account balance or key balance, today's charges, and the running total of keys without a limit. Amounts are converted with the currency and exchange rate the site uses.

### Sub2API

Enter the site address and API key. Wallet mode shows the account balance and today's charges; subscription mode shows daily, weekly and monthly limits; limited keys show their limits for each period and the key balance. The tag next to the name shows the rate multiplier or the plan name.

If you pick New API for a Sub2API site, or the other way round, the result suggests the right one.

### Custom script

When you choose Custom, the code box is filled with a template that runs as is. The script defines `fetchUsage(ctx)` and returns the quotas to show:

```javascript
async function fetchUsage(ctx) {
  const res = await ctx.http.get(ctx.site + "/api/usage", {
    headers: { Authorization: "Bearer " + ctx.secrets.key }
  });
  if (res.status !== 200) throw new Error("Read failed: HTTP " + res.status);
  const data = res.json();
  return [
    { title: "This month", percent: data.used / data.limit * 100, value: "$" + data.used.toFixed(2), suffix: "used", resetAt: data.resetText }
  ];
}
```

`ctx` provides:

| Member | Description |
| --- | --- |
| `ctx.site`, `ctx.secrets.key` | The site address and API key you entered; empty strings when not filled in |
| `ctx.http.get(url, options)`, `ctx.http.post(url, options)` | Sends a request. `options` may contain `headers`, `timeout` (milliseconds) and `body` (POST only). Returns `{ status, headers, text(), json() }`. The address must use HTTPS, by the same rules as the site address |
| `ctx.log(text)` | Writes a log line shown by Test connection |
| `ctx.language`, `ctx.now` | The interface language (`"zh-CN"` or `"en-US"`) and the time of this read |

Each item of the returned array is one quota, shown in order:

| Field | Description |
| --- | --- |
| `title` | Required. The quota name |
| `percent` | Optional, 0–100. The share used; sets the length and color of the bar |
| `value`, `suffix` | Optional. The large figure and the small text to its right, such as `"$12.30"` and `"used"` |
| `note` | Optional. Shown at the right of the name's line |
| `resetAt` | Optional. Shown at the right of the figure's line, as written, such as `"in 2h 13m"` |

Items that do not meet these rules are skipped, and the result explains why. Errors thrown by the script are shown as they are.

Scripts run in a sandbox with no access to files, processes or .NET objects. Each read may take up to 10 seconds and send up to 16 requests, with requests and responses of up to 1 MB each.
