using System.Net;
using System.Text;
using BrimDeck.Core;

// A catalog entry carries the cheapest host's price; a model that records were priced with takes its maker's own endpoint price.
static class OfficialPriceTests
{
    public static async Task Run(string root, Action<string, bool> check)
    {
        var folder = Path.Combine(root, "official-prices"); Directory.CreateDirectory(folder);
        static TokenPrices Price(int n) => new(n * 1e-6m, n * 5e-6m, n * 1e-7m, null, null);
        static string Json(TokenPrices p) => $$"""{"prompt":"{{p.Input}}","completion":"{{p.Output}}","input_cache_read":"{{p.CacheRead}}"}""";
        var catalog = new List<(string Id, int Price)> { ("z-ai/glm-5.3", 1), ("x-ai/grok-4.7", 2), ("deepseek/deepseek-v4-pro", 3), ("google/gemini-4-argon", 1) };
        for (int i = 0; i < 20; i++) catalog.Add(($"acme/model-{i}", 1));
        string catalogJson = "{\"data\":[" + string.Join(",", catalog.Select(x => $$"""{"id":"{{x.Id}}","pricing":{{Json(Price(x.Price))}}}""")) + "]}";
        static string Endpoint(string tag, int price) => $$"""{"provider_name":"x","tag":"{{tag}}","pricing":{{Json(Price(price))}}}""";
        static string Endpoints(params string[] items) => "{\"data\":{\"endpoints\":[" + string.Join(",", items) + "]}}";
        var endpoints = new Dictionary<string, string>
        {
            ["z-ai/glm-5.3"] = Endpoints(Endpoint("wafer/us", 1), Endpoint("mistral/zdr", 4), Endpoint("alibaba", 6), Endpoint("z-ai/fp8", 5)),
            ["x-ai/grok-4.7"] = Endpoints(Endpoint("xai/zdr", 9), Endpoint("xai", 6), Endpoint("xai/priority", 12)),
            ["google/gemini-4-argon"] = Endpoints(Endpoint("google-ai-studio/flex", 1), Endpoint("google-vertex/global", 8), Endpoint("google-ai-studio", 7)),
            ["deepseek/deepseek-v4-pro"] = Endpoints(Endpoint("streamlake/fp8", 2), Endpoint("azure/us", 4)),
        };
        var now = DateTimeOffset.Parse("2026-10-26T12:00:00Z");
        bool fail = false; var asked = new List<string>();
        using var http = new HttpClient(new Server(uri =>
        {
            if (fail) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("") };
            var path = uri.AbsolutePath;
            if (path == "/api/v1/models") return Reply(catalogJson);
            var model = path["/api/v1/models/".Length..^"/endpoints".Length];
            lock (asked) asked.Add(model);
            return Reply(endpoints.GetValueOrDefault(model, "{}"));
        }));
        TokenEntry Record(string model) => new("dsh:" + model, now, model, 1000, 0, 0, 0, 100);
        static decimal Cost(TokenPrices p) => 1000 * p.Input + 100 * p.Output;

        using (var pricing = new Pricing(folder, http, () => now))
        {
            await pricing.RefreshAsync();
            check("The first refresh asks only for the models Kimi Code always needs", asked.Order().SequenceEqual(Pricing.OfficialModels.Order()));
            check("Before a model's own price is read, its records take the catalog price",
                pricing.Estimate(Record("glm-5.3")) == Cost(Price(1)) && pricing.Estimate(Record("grok-4.7")) == Cost(Price(2)) &&
                pricing.Estimate(Record("gemini-4-argon")) == Cost(Price(1)) && pricing.Estimate(Record("deepseek-v4-pro")) == Cost(Price(3)));
            check("A record with a recorded cost never asks for a price", pricing.Estimate(Record("acme/model-0") with { ReportedCostUsd = 0.5m }) == 0.5m);
            asked.Clear();
            await pricing.RefreshAsync();
            check("The next refresh asks for the maker's price of each model records were priced with, without the catalog again",
                asked.Order().SequenceEqual(new[] { "deepseek/deepseek-v4-pro", "google/gemini-4-argon", "x-ai/grok-4.7", "z-ai/glm-5.3" }));
            check("GLM-5.3 takes Z.AI's price, not the cheapest host's or another host's", pricing.Estimate(Record("glm-5.3")) == Cost(Price(5)));
            check("A maker whose endpoints use another slug is still found, without its zero-retention or priority tiers",
                pricing.Estimate(Record("grok-4.7")) == Cost(Price(6)) && pricing.Estimate(Record("gemini-4-argon")) == Cost(Price(7)));
            check("A model without its maker's endpoint keeps the catalog price", pricing.Estimate(Record("deepseek-v4-pro")) == Cost(Price(3)));
            check("The price list shows the official prices", pricing.Models.Single(m => m.Model == "z-ai/glm-5.3").Prices == Price(5));
            asked.Clear();
            await pricing.RefreshAsync();
            check("Each model is asked for once a day", asked.Count == 0);

            asked.Clear();
            for (int i = 0; i < 20; i++) pricing.Estimate(Record($"model-{i}"));
            await pricing.RefreshAsync();
            int first = asked.Count(m => m.StartsWith("acme/", StringComparison.Ordinal));
            await pricing.RefreshAsync();
            check("A refresh asks for at most 16 models; the rest follow on the next", first == 16 && asked.Count(m => m.StartsWith("acme/", StringComparison.Ordinal)) == 20 &&
                asked.Distinct().Count() == asked.Count);

            asked.Clear();
            now = now.AddDays(1);
            endpoints["z-ai/glm-5.3"] = Endpoints(Endpoint("wafer/us", 1));
            await pricing.RefreshAsync(); await pricing.RefreshAsync();
            check("A day later the models are asked for again", asked.Count == 26 && asked.Contains("z-ai/glm-5.3") && asked.Contains("x-ai/grok-4.7"));
            check("An answer without the maker's endpoint keeps the official price read before", pricing.Estimate(Record("glm-5.3")) == Cost(Price(5)));
        }

        fail = true;
        using (var restarted = new Pricing(folder, http, () => now))
        {
            await restarted.RefreshAsync();
            check("After a restart without a connection the saved official prices apply", restarted.Estimate(Record("glm-5.3")) == Cost(Price(5)) &&
                restarted.Estimate(Record("grok-4.7")) == Cost(Price(6)));
        }

        var legacyFolder = Path.Combine(root, "official-prices-legacy"); Directory.CreateDirectory(legacyFolder);
        File.WriteAllText(Path.Combine(legacyFolder, Pricing.LegacyOfficialFile),
            $$"""{"updatedAt":"2026-10-20T00:00:00Z","models":[{"Model":"moonshotai/kimi-k3","Prices":{"Input":0.000003,"Output":0.000015,"CacheRead":0.0000003,"CacheWrite":null,"CacheWriteHour":null},"Tiers":[]}]}""");
        using (var legacy = new Pricing(legacyFolder, http, () => now))
            check("Moonshot AI prices saved by earlier versions are still read", legacy.Models.SingleOrDefault(m => m.Model == "moonshotai/kimi-k3") is { Prices.Input: 0.000003m });

        check("Endpoints whose tag names another maker or a non-quantization variant are left out",
            Pricing.ParseOfficial(System.Text.Json.JsonDocument.Parse(endpoints["x-ai/grok-4.7"]).RootElement, "x-ai/grok-4.7").Select(m => m.Prices).SequenceEqual([Price(6)]) &&
            Pricing.ParseOfficial(System.Text.Json.JsonDocument.Parse(endpoints["deepseek/deepseek-v4-pro"]).RootElement, "deepseek/deepseek-v4-pro").Count == 0 &&
            Pricing.ParseOfficial(System.Text.Json.JsonDocument.Parse(endpoints["x-ai/grok-4.7"]).RootElement, "grok-4.7").Count == 0);
    }

    private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Server(Func<Uri, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request.RequestUri!));
    }
}
