using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

public static class PlanParser
{
    public static string Cursor(JsonElement root) => root.Get("membershipType").Text().Trim();

    public static string Antigravity(JsonElement root)
    {
        var status = root.Get("userStatus");
        var tier = status.Get("userTier").Get("name").Text().Trim();
        return tier.Length > 0 ? tier : status.Get("planStatus").Get("planInfo").Get("planName").Text().Trim();
    }

    // Claude Code's local account record. On a Team seat the organization's tier is the Team's default ("default_raven") and the
    // seat's own tier is userRateLimitTier.
    public static string ClaudeProfile(JsonElement root)
    {
        var account = root.Get("oauthAccount");
        var type = account.Get("organizationType").Text();
        if (ClaudeOrganizationType(type, account.Get("seatTier").Text(), account.Get("userRateLimitTier").Text()) is { Length: > 0 } organization) return organization;
        var plan = ClaudeTier(account.Get("organizationRateLimitTier").Text());
        return plan.Length > 0 ? plan : ClaudePersonalType(type);
    }

    // A Team or Enterprise seat can carry a Max tier ("default_claude_max_5x" on a Team Premium seat); the organization decides.
    // A sign-in record names no seat, so its tier alone tells the Team seat.
    // Anthropic renamed Max 20x to Ultra and Max 5x to Max in October 2026 and kept the tier codes: accounts already on Ultra
    // still carry "default_claude_max_20x". "default_claude_ultra" has not been seen and is read in case the code changes too.
    // A multiplier with no current name keeps its number.
    public static string ClaudeTier(string tier, string subscription = "")
    {
        if (ClaudeOrganizationType("claude_" + subscription.Trim().ToLowerInvariant(), "", tier) is { Length: > 0 } organization) return organization;
        if (Regex.IsMatch(tier, @"(?:^|_)claude_(?:max_20x|ultra)$", RegexOptions.CultureInvariant)) return "Ultra";
        var max = Regex.Match(tier, @"(?:^|_)claude_max_(\d+x)$", RegexOptions.CultureInvariant);
        if (!max.Success) return subscription.Trim();
        return max.Groups[1].Value == "5x" ? "Max" : "Max " + max.Groups[1].Value;
    }

    // Team seats come as Standard and Premium. Anthropic documents no names for the codes; these were seen on live accounts:
    // seat "team_standard" for Standard, "team_tier_1" with the tier "default_claude_max_5x" for Premium (five times the usage
    // of a Standard seat). A seat that is not named counts as Premium on that tier, and otherwise stays "Team".
    private static string ClaudeOrganizationType(string type, string seat, string tier) => type switch
    {
        "claude_team" => seat switch
        {
            "team_standard" => "Team Standard",
            "team_tier_1" => "Team Premium",
            _ => Regex.IsMatch(tier, @"(?:^|_)claude_max_5x$", RegexOptions.CultureInvariant) ? "Team Premium" : "Team"
        },
        "claude_enterprise" => "Enterprise",
        _ => ""
    };

    // The account endpoint (/api/oauth/profile) names the organization's tier and type. It follows plan changes at once,
    // while the tier cached beside a sign-in token only changes when the token is issued again. Its plan_display_name is the
    // name Anthropic shows ("Ultra"), so a later rename needs no change here; Team and Enterprise keep their seat names.
    public static string ClaudeOrganization(JsonElement root)
    {
        var organization = root.Get("organization");
        var type = organization.Get("organization_type").Text();
        if (ClaudeOrganizationType(type, organization.Get("seat_tier").Text(), organization.Get("rate_limit_tier").Text()) is { Length: > 0 } shared) return shared;
        if (organization.Get("plan_display_name").Text().Trim() is { Length: > 0 } shown) return shown;
        var plan = ClaudeTier(organization.Get("rate_limit_tier").Text());
        return plan.Length > 0 ? plan : ClaudePersonalType(type);
    }

    // A personal subscription whose tier names no multiplier.
    private static string ClaudePersonalType(string type) => type switch
    {
        "claude_max" => "Max",
        "claude_pro" => "Pro",
        _ => ""
    };

    // Combines the plan a sign-in token carries with the one in the local account record. Claude Code refreshes the local
    // record on every start, while the token's copy only changes when the token is issued again, so the local record wins.
    public static string ReconcileClaude(string signIn, string local)
    {
        signIn = signIn.Trim(); local = local.Trim();
        if (local.Length == 0) return signIn;
        return local;
    }
}
