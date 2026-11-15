using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrimDeck.Core;

public static class ProviderCatalog
{
    public static readonly ProviderId[] BuiltIns = [ProviderId.Claude, ProviderId.Codex, ProviderId.Antigravity, ProviderId.Cursor];
    // Sources read automatically from local applications; only the first four are added to new settings by default.
    public static readonly ProviderId[] AutomaticSources = [.. BuiltIns, ProviderId.GrokBot, ProviderId.ZCode, ProviderId.Dsh, ProviderId.Kimi, ProviderId.OpenCode, ProviderId.CodeAgent, ProviderId.CommandCode, ProviderId.WorkBuddy, ProviderId.WorkBuddyAi, ProviderId.MiMo, ProviderId.MiMoLinked];
    // Xiaomi MiMo is read like the automatic sources but needs a sign-in in settings, so the menu lists it with the sources entered by
    // hand, next to Xiaomi MiMo API and Token Plan.
    public static readonly ProviderId[] Sources = [.. AutomaticSources.Where(id => id != ProviderId.MiMo), ProviderId.GlmChina, ProviderId.GlmGlobal, ProviderId.MiMo, ProviderId.MiMoApi, ProviderId.MiMoTokenPlan, ProviderId.DeepSeek, ProviderId.KimiApi, ProviderId.KimiPlatform, ProviderId.OpenCodeGo, ProviderId.CommandCodeApi, ProviderId.OpenRouter, ProviderId.NewApi, ProviderId.Sub2Api, ProviderId.Custom];
    // Grok Bot is quota-only; Pi is usage-only and must never appear in the quota-source menu.
    public static readonly ProviderId[] UsageSources = [.. BuiltIns, ProviderId.ZCode, ProviderId.Dsh, ProviderId.Kimi, ProviderId.OpenCode, ProviderId.Pi, ProviderId.CodeAgent, ProviderId.CommandCode, ProviderId.WorkBuddy, ProviderId.WorkBuddyAi, ProviderId.MiMo];
    public static bool IsBuiltIn(ProviderId id) => AutomaticSources.Contains(id);
    public static bool IsUsageSource(ProviderId id) => UsageSources.Contains(id);
    // Which clients a statistics source covers; each reads the local records all of its clients share.
    public static string UsageTip(ProviderId id) => id switch
    {
        ProviderId.Pi => Loc.T("Pi 本机会话（所有提供商）", "Local Pi sessions (all providers)"),
        ProviderId.CodeAgent => Loc.T("CodeAgent CLI 本机会话", "Local CodeAgent CLI sessions"),
        ProviderId.WorkBuddy => Loc.T("WorkBuddy 桌面版本机会话", "Local WorkBuddy desktop app sessions"),
        ProviderId.WorkBuddyAi => Loc.T("WorkBuddy AI 桌面版本机会话", "Local WorkBuddy AI desktop app sessions"),
        ProviderId.Claude => Loc.T("Claude Code + Claude 桌面版", "Claude Code + Claude desktop app"),
        ProviderId.Codex => Loc.T("Codex 桌面版 + Codex CLI", "Codex desktop app + Codex CLI"),
        ProviderId.Antigravity => Loc.T("Antigravity 桌面版 + Antigravity CLI", "Antigravity desktop app + Antigravity CLI"),
        ProviderId.ZCode => Loc.T("ZCode 桌面应用", "ZCode desktop app"),
        ProviderId.Dsh => Loc.T("DeepSeek Harness 桌面版 + 本地 Web 版", "DeepSeek Harness desktop app + local web app"),
        ProviderId.Kimi => Loc.T("Kimi Code CLI + 桌面版 + VS Code 扩展", "Kimi Code CLI + desktop app + VS Code extension"),
        ProviderId.OpenCode => Loc.T("OpenCode CLI + 桌面版", "OpenCode CLI + desktop app"),
        ProviderId.CommandCode => Loc.T("Command Code CLI + 桌面版", "Command Code CLI + desktop app"),
        ProviderId.MiMo => Loc.T("MiMo Code CLI + Xiaomi MiMo 桌面版", "MiMo Code CLI + Xiaomi MiMo desktop app"),
        ProviderId.Cursor => Loc.T("Cursor 账户用量，包含其他设备", "Cursor account usage, including other devices"),
        _ => Name(id)
    };
    // Antigravity quotas come only from the desktop app's local service; the CLI's own service cannot be queried.
    public static string QuotaDescription(ProviderId id) => id switch
    {
        ProviderId.Antigravity => Loc.T("额度读取自 Antigravity 桌面版，需要桌面版正在运行；不读取 Antigravity CLI",
            "The quota is read from the Antigravity desktop app, which must be running; Antigravity CLI is not read"),
        ProviderId.Dsh => Loc.T("DeepSeek Harness 桌面版或本地 Web 版登录的 DeepSeek 账户余额；未登录账户时使用其中保存的 API 密钥",
            "The balance of the DeepSeek account that the DeepSeek Harness desktop app or local web app signed in with; without a signed-in account, the API key saved there is used"),
        ProviderId.GrokBot => Loc.T("只显示 Grok Bot 桌面版登录账户的每周额度",
            "Only the weekly allowance of the account signed in to the Grok Bot desktop app is shown"),
        ProviderId.Kimi => Loc.T("Kimi Code 登录账户的会员套餐额度；不续期登录",
            "The membership plan quota of the account signed in to Kimi Code; the sign-in is never renewed"),
        ProviderId.OpenCode => Loc.T("OpenCode 登录 OpenCode Console 的工作区的 OpenCode Go 额度；不续期登录",
            "The OpenCode Go allowance of the workspace OpenCode signed in to OpenCode Console with; the sign-in is never renewed"),
        ProviderId.CodeAgent => Loc.T("CodeAgent 登录账户的积分额度，所有模型共用；登录过期时用 CodeAgent CLI 自动续期",
            "The credit allowance of the account signed in to CodeAgent, shared by every model; an expired sign-in is renewed with CodeAgent CLI"),
        ProviderId.CommandCode => Loc.T("Command Code 登录账户的套餐额度：5 小时、每周与每月额度，以及额外积分",
            "The plan quota of the account signed in to Command Code: the 5-hour, weekly and monthly limits, and extra credits"),
        ProviderId.WorkBuddy => Loc.T("WorkBuddy 国内版账户的积分余额，需要在设置中通过浏览器登录 WorkBuddy 官网",
            "The credits of a WorkBuddy China edition account; sign in on the WorkBuddy website in the browser from Settings first"),
        ProviderId.WorkBuddyAi => Loc.T("WorkBuddy 国际版（WorkBuddy AI）账户的积分余额，需要在设置中通过浏览器登录 WorkBuddy 国际版官网",
            "The credits of a WorkBuddy AI (international edition) account; sign in on the WorkBuddy AI website in the browser from Settings first"),
        ProviderId.MiMo => Loc.T("Xiaomi MiMo 桌面版会员的每周额度，需要在设置中登录小米账号；不包含 Token Plan",
            "The weekly allowance of the Xiaomi MiMo desktop membership; sign in to a Xiaomi account in Settings first. Token Plan is not included"),
        ProviderId.MiMoLinked => Loc.T("自动读取 Xiaomi MiMo 桌面版与 MiMo Code CLI 已关联的 API 密钥或 Token Plan", "Reads the API key or Token Plan linked in the Xiaomi MiMo desktop app and MiMo Code CLI"),
        ProviderId.MiMoApi => Loc.T("按量计费 API 密钥所属的小米 MiMo 开放平台账户", "The Xiaomi MiMo Open Platform account of the pay-as-you-go API key"),
        ProviderId.KimiApi => Loc.T("用 Kimi Code 控制台的密钥读取会员套餐额度", "The membership plan quota, read with a key from the Kimi Code console"),
        ProviderId.KimiPlatform => Loc.T("Kimi 开放平台按量计费的账户余额，与会员额度分开计算",
            "The pay-as-you-go balance of the Kimi Open Platform account, billed apart from the membership quota"),
        ProviderId.CommandCodeApi => Loc.T("API 密钥所属账户的 Command Code 套餐额度", "The Command Code plan quota of the account the API key belongs to"),
        ProviderId.OpenCodeGo => Loc.T("API 密钥所属工作区的 OpenCode Go 额度", "The OpenCode Go allowance of the workspace the API key belongs to"),
        ProviderId.OpenRouter => Loc.T("API 密钥所属账户的余额，以及这个密钥的限额与消费", "The balance of the account the API key belongs to, and the limit and spending of this key"),
        _ => ""
    };
    // The statistics-app menu names Xiaomi MiMo without "Desktop subscription": its records are MiMo Code's and the desktop app's.
    public static string UsageName(ProviderId id) => id == ProviderId.MiMo ? "Xiaomi MiMo" : Name(id);
    public static string QuotaTip(ProviderId id) => Loc.T("配额来源：", "Quota source: ") + Name(id) + (QuotaDescription(id) is { Length: > 0 } text ? "\n" + text : "");
    // The quota-source menu groups sources by whether the user has to fill anything in.
    public static string Group(ProviderId id) => IsBuiltIn(id) && id != ProviderId.MiMo ? Loc.T("自动读取", "Read automatically") : Loc.T("手动填写", "Entered by hand");
    public static string Name(ProviderId id) => id switch
    {
        ProviderId.GlmChina => Loc.T("智谱 GLM", "Zhipu GLM"), ProviderId.GlmGlobal => "Z.ai GLM", ProviderId.Dsh => "DeepSeek Harness", ProviderId.GrokBot => "Grok Bot", ProviderId.Kimi => "Kimi Code", ProviderId.KimiApi => "Kimi Code API", ProviderId.KimiPlatform => Loc.T("Kimi 开放平台", "Kimi Open Platform"),ProviderId.OpenCode => "OpenCode", ProviderId.OpenCodeGo => "OpenCode Go", ProviderId.CommandCode => "Command Code", ProviderId.CommandCodeApi => "Command Code API", ProviderId.WorkBuddy => "WorkBuddy", ProviderId.WorkBuddyAi => "WorkBuddy AI", ProviderId.MiMo => Loc.T("Xiaomi MiMo 桌面订阅", "Xiaomi MiMo Desktop subscription"), ProviderId.MiMoApi => "Xiaomi MiMo API", ProviderId.MiMoLinked => "Xiaomi MiMo", ProviderId.MiMoTokenPlan => "Xiaomi MiMo Token Plan", ProviderId.OpenRouter => "OpenRouter", ProviderId.DeepSeek => "DeepSeek", ProviderId.NewApi => "New API", ProviderId.Sub2Api => "Sub2API", ProviderId.Custom => Loc.T("自定义", "Custom"),
        _ => id.ToString()
    };
    public static string Key(ProviderId id) => id switch
    {
        ProviderId.GlmChina => "glm-cn", ProviderId.GlmGlobal => "glm-global", ProviderId.NewApi => "newapi", ProviderId.Sub2Api => "sub2api", ProviderId.KimiApi => "kimi-api", ProviderId.KimiPlatform => "kimi-platform",ProviderId.OpenCodeGo => "opencode-go", ProviderId.CommandCodeApi => "commandcode-api", ProviderId.MiMoApi => "mimo-api", ProviderId.MiMoLinked => "mimo-linked", ProviderId.MiMoTokenPlan => "mimo-token-plan",
        _ => id.ToString().ToLowerInvariant()
    };
    // Claude and Grok Bot rows each choose between the account API and the record the desktop app keeps on this PC.
    public static bool HasOnlineSwitch(ProviderId id) => id is ProviderId.Claude or ProviderId.GrokBot;
    // Marks the snapshot of such a source built only from this PC's records, shown by its rows that do not read online.
    public const string LocalQuota = "local";
    public static bool ReadsLocalQuota(AppEntry entry) => HasOnlineSwitch(entry.QuotaSource) && !entry.QuotaOnline;
    // Sources billed per API call whose account balance comes without a total; their rows can set a target balance
    // (AppEntry.BalanceTarget) that gives the balance a used share.
    public static bool HasAccountBalance(ProviderId id) => id is ProviderId.Dsh or ProviderId.DeepSeek or ProviderId.KimiPlatform or ProviderId.MiMoApi or ProviderId.MiMoLinked or ProviderId.OpenRouter or ProviderId.NewApi or ProviderId.Sub2Api;
    public static bool IsAccountBalance(UsageMetric metric) => metric is { Kind: MetricKind.Balance, Label: "账户余额", Total: null };
    // Rows that allow HTTP are read apart from otherwise identical rows that do not; the keys of other rows stay as they were.
    public static string ConfigurationKey(AppEntry entry) => Hash(JsonSerializer.Serialize(new { entry.QuotaSource, entry.Site, entry.Script, entry.SecretRevision }))
        + (ReadsLocalQuota(entry) ? ":" + LocalQuota : "") + (entry.AllowHttp ? ":" + HttpAllowed : "");
    public static string RequestKey(AppEntry entry, string secret) => Hash(JsonSerializer.Serialize(new { entry.QuotaSource, entry.Site, entry.Script, secret }))
        + (entry.AllowHttp ? ":" + HttpAllowed : "");
    private const string HttpAllowed = "http";
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    // Console pages people copy from the address bar; everything from the first such segment on is dropped.
    private static readonly string[] FrontendPages = ["console", "dashboard", "panel", "login", "pricing", "keys"];
    public static string NormalizeSite(string site, bool removeV1 = true)
    {
        site = site.Trim().TrimEnd('/');
        if (removeV1 && Uri.TryCreate(site, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int page = Array.FindIndex(segments, s => FrontendPages.Contains(s, StringComparer.OrdinalIgnoreCase));
            if (page >= 0) site = uri.GetLeftPart(UriPartial.Authority) + string.Concat(segments[..page].Select(s => "/" + s));
        }
        if (removeV1 && site.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) site = site[..^3].TrimEnd('/');
        return site;
    }
    public static bool NeedsSite(ProviderId id) => id is ProviderId.NewApi or ProviderId.Sub2Api or ProviderId.Custom;
    public static Uri ValidateUrl(string url, bool allowHttp = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || string.IsNullOrEmpty(uri.Host))
            throw new InvalidDataException(allowHttp
                ? Loc.T("请填写有效的 HTTP 或 HTTPS 站点地址，不要包含用户名、密码或 # 及其后的内容。", "Enter a valid HTTP or HTTPS site address, without a user name, password, or # and what follows it.")
                : Loc.T("请填写有效的 HTTPS 站点地址，不要包含用户名、密码或 # 及其后的内容。", "Enter a valid HTTPS site address, without a user name, password, or # and what follows it."));
        if (NeedsHttps(uri, allowHttp)) throw new InvalidDataException(HttpsRequired);
        return uri;
    }
    // Requests carry the API key, so they go over HTTPS. Plain HTTP reaches a service on this PC (a local gateway), and any
    // address once the row allows HTTP (AppEntry.AllowHttp).
    public static bool NeedsHttps(Uri uri, bool allowHttp = false) => !allowHttp && uri.Scheme == Uri.UriSchemeHttp && !IsLoopback(uri);
    private static bool IsLoopback(Uri uri) => uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    public static string HttpsRequired => Loc.T("须使用 HTTPS；HTTP 仅限本机地址", "Use HTTPS; HTTP is only for this PC");
}

// Write stable source identifiers; existing numeric enum settings remain readable.
public sealed class QuotaSourceConverter : JsonConverter<ProviderId>
{
    public override ProviderId Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var id)) return (ProviderId)id;
        if (reader.TokenType == JsonTokenType.String)
        {
            var key = reader.GetString();
            foreach (var source in ProviderCatalog.Sources)
                if (string.Equals(key, ProviderCatalog.Key(source), StringComparison.OrdinalIgnoreCase) || string.Equals(key, source.ToString(), StringComparison.OrdinalIgnoreCase)) return source;
        }
        return (ProviderId)(-1);
    }
    public override void Write(Utf8JsonWriter writer, ProviderId value, JsonSerializerOptions options) => writer.WriteStringValue(ProviderCatalog.Key(value));
}
