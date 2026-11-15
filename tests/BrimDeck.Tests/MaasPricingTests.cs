using BrimDeck.Core;

// Model names of MaaS gateways (CodeAgent, and other tools that reach its models through a proxy) take the price of the catalog
// model they serve, whatever tool recorded them (Pricing.MaasName). The names are those found in CodeAgent records and its model list.
static class MaasPricingTests
{
    public static void Run(string root, Action<string, bool> check)
    {
        var folder = Path.Combine(root, "maas-prices");
        Directory.CreateDirectory(folder);
        TokenPrices Price(int n) => new(n * .000001m, n * .000005m, n * .0000001m, null, null);
        var catalog = new (string Id, int N)[]
        {
            ("z-ai/glm-5", 1), ("z-ai/glm-5.1", 2), ("z-ai/glm-5.2", 3), ("z-ai/glm-5.2:batch", 30), ("z-ai/glm-5.3", 4), ("z-ai/glm-5.3-flash", 5),
            ("deepseek/deepseek-v4-flash", 6), ("deepseek/deepseek-v4-flash-0731", 7), ("deepseek/deepseek-v4.1-flash", 8), ("deepseek/deepseek-v4.1-flash-1017", 9),
            ("qwen/qwen3.7-plus", 10), ("qwen/qwen3.7-flash", 11), ("qwen/qwen3.7-max", 12), ("qwen/qwen3.8-flash", 13),
            ("minimax/minimax-m3", 14), ("minimax/minimax-m3.1-flash", 15)
        };
        File.WriteAllText(Path.Combine(folder, "model-prices.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            updatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            models = catalog.Select(x => new ModelPrice(x.Id, Price(x.N), [])).ToArray()
        }));
        using var pricing = new Pricing(folder);
        var entry = new TokenEntry("claude:maas", DateTimeOffset.Now, "", 1000, 2000, 0, 0, 100);
        decimal Cost(int n) { var p = Price(n); return 1000 * p.Input + 100 * p.Output + 2000 * p.CacheRead!.Value; }
        bool All(int n, params string[] names) => names.All(name => pricing.Estimate(entry with { Model = name }) == Cost(n));
        bool None(params string[] names) => names.All(name => pricing.Estimate(entry with { Model = name }) is null);

        check("MaaS GLM names drop the gateway, provider and channel words",
            All(2, "maas-glm-5.1", "maas-glm-5.1-zhipu") && All(3, "maas-glm-5.2", "maas-glm-5.2-tencent-codeagent", "maas-glm-5.2-volcengine-codeagent")
            && All(4, "maas-glm-5.3-baidu-codeagent", "maas-glm-5.3-volcengine-codeagent", "maas-glm-5.3-zhipu-codeagent", "maas-glm-5.3-tencent-codeagent"));
        check("MaaS release suffixes and dashed versions are read", All(3, "maas-glm-5-2-260617") && All(6, "maas-deepseek-v4-flash-ga-260731")
            && All(8, "maas-deepseek-v4-1-flash-261017", "maas-deepseek-v4.1-flash-1017"));
        check("MaaS DeepSeek, Qwen and MiniMax names match in any case",
            All(6, "maas-DeepSeek-V4-Flash-volcengine-codeagent") && All(8, "maas-deepseek-v4.1-flash-baidu-codeagent", "maas-deepseek-v4.1-flash-volcengine-codeagent")
            && All(10, "maas-qwen3.7-plus") && All(11, "maas-qwen3.7-flash") && All(12, "maas-qwen3.7-max") && All(13, "maas-qwen3.8-flash-aliyun-codeagent")
            && All(14, "maas-MiniMax-M3"));
        check("MaaS automatic routes take the price of the model they are named after",
            All(3, "GLM-5.2-Auto(Tencent-MaaS)", "GLM-5.2-Auto(Volcano-MaaS)") && All(4, "GLM-5.3-Auto(Baidu-MaaS)", "GLM-5.3-Auto(Volcano-MaaS)"));
        check("A new provider or channel word needs no change", All(4, "maas-glm-5.3-newcloud-codeagent", "maas-glm-5.3-newcloud"));
        check("A later version takes its own price once listed and is never priced as another version",
            None("maas-glm-5.4-volcengine-codeagent", "GLM-5.4-Auto(Volcano-MaaS)", "maas-qwen3.9-plus", "maas-deepseek-v5-flash-baidu-codeagent"));
        check("A dropped word that names another model of the family leaves the name unpriced",
            None("maas-glm-5.3-tencent-flash", "maas-glm-5.3-baidu-flash-codeagent") && All(5, "maas-glm-5.3-flash-tencent-codeagent"));
        check("Only MaaS names are read this way", None("glm-5.3-volcengine-codeagent", "deepseek-v4-flash-ga-260731", "qwen3.8-flash-aliyun", "maas-", "maas-codeagent"));
        check("An exact manual price wins over the MaaS rule",
            pricing.TrySetManual("maas-glm-5.2-tencent-codeagent", Price(20), false, out _) && All(20, "maas-glm-5.2-tencent-codeagent") && All(3, "maas-glm-5.2-volcengine-codeagent"));

        File.WriteAllText(Path.Combine(folder, "model-prices.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            updatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            models = catalog.Append((Id: "z-ai/glm-5.4", N: 16)).Select(x => new ModelPrice(x.Id, Price(x.N), [])).ToArray()
        }));
        using var later = new Pricing(folder);
        check("A later version is priced as soon as the catalog lists it",
            later.Estimate(entry with { Model = "maas-glm-5.4-volcengine-codeagent" }) == Cost(16) && later.Estimate(entry with { Model = "GLM-5.4-Auto(Baidu-MaaS)" }) == Cost(16));
    }
}
