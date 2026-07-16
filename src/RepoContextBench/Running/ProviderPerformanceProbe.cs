using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeAlive.Agents.Clients;
using CodeAlive.Domain;
using CodeAlive.Domain.Configs;

namespace RepoContextBench.Running;

/// <summary>
/// Measures OpenAI-compatible streaming performance without persisting credentials or model output.
/// </summary>
public sealed class ProviderPerformanceProbe
{
    private const string Prompt = """
        Write 160 to 190 English words describing a C# method that retries a transient HTTP request.
        Include exactly five bullet points and one short fenced C# code block. Be concise and do not
        mention the model, provider, benchmark, or these instructions.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task Run(RepoContextBenchRunCommand command, LlmConfig config, CancellationToken cancellationToken)
    {
        string providerValue = command.AnswererProviderOverride
            ?? throw new ArgumentException("perf-probe requires --provider.");
        if (!Enum.TryParse(providerValue, ignoreCase: true, out LlmProvider provider))
        {
            throw new ArgumentException($"Unsupported provider '{providerValue}'.");
        }

        string model = command.AnswererModelOverride
            ?? throw new ArgumentException("perf-probe requires --model.");
        string reasoningEffort = command.AnswererReasoningEffortOverride ?? "high";
        (string apiKey, string baseUrl) = LlmChatClientFactory.ResolveProviderConfig(provider, config, customBaseUrl: null);
        Uri endpoint = new($"{baseUrl.TrimEnd('/')}/chat/completions", UriKind.Absolute);

        using HttpClient client = new() { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        List<ProviderPerformanceSample> samples = new();

        for (int index = 0; index < command.PerformanceProbeWarmup + command.PerformanceProbeSamples; index++)
        {
            ProviderPerformanceSample sample = await Measure(
                client,
                endpoint,
                provider.ToString(),
                model,
                reasoningEffort,
                command.PerformanceProbeMaxTokens,
                cancellationToken);

            bool warmup = index < command.PerformanceProbeWarmup;
            await Console.Out.WriteLineAsync(
                $"[{(warmup ? "warmup" : "sample")} {index + 1}] " +
                $"ttft={sample.TimeToFirstGeneratedTokenMs:F0}ms " +
                $"ttfv={FormatMilliseconds(sample.TimeToFirstVisibleTokenMs)} " +
                $"e2e={sample.EndToEndMs:F0}ms tps={sample.ReportedCompletionTokensPerSecond:F1} " +
                $"finish={sample.FinishReason ?? "unknown"}");
            if (!warmup)
            {
                samples.Add(sample);
            }
        }

        ProviderPerformanceReport report = ProviderPerformanceReport.Create(
            provider.ToString(),
            model,
            reasoningEffort,
            endpoint.ToString(),
            command.PerformanceProbeWarmup,
            command.PerformanceProbeMaxTokens,
            samples);
        string outputPath = Path.GetFullPath(command.Out);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
        await Console.Out.WriteLineAsync($"Wrote {outputPath}");
    }

    private static async Task<ProviderPerformanceSample> Measure(
        HttpClient client,
        Uri endpoint,
        string provider,
        string model,
        string reasoningEffort,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        object payload = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = "You are a precise software engineering assistant." },
                new { role = "user", content = Prompt },
            },
            temperature = 0,
            max_tokens = maxTokens,
            stream = true,
            stream_options = new { include_usage = true },
            reasoning_effort = reasoningEffort,
        };

        using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        Stopwatch stopwatch = Stopwatch.StartNew();
        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        double headersMs = stopwatch.Elapsed.TotalMilliseconds;
        double? firstGeneratedMs = null;
        double? firstVisibleMs = null;
        int? promptTokens = null;
        int? completionTokens = null;
        int? totalTokens = null;
        string? finishReason = null;
        int visibleCharacters = 0;
        int reasoningCharacters = 0;

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using StreamReader reader = new(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            string json = line["data:".Length..].Trim();
            if (json.Length == 0 || string.Equals(json, "[DONE]", StringComparison.Ordinal))
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (TryGetDelta(root, out bool generated, out bool visible))
            {
                if (generated && firstGeneratedMs is null)
                {
                    firstGeneratedMs = stopwatch.Elapsed.TotalMilliseconds;
                }

                if (visible && firstVisibleMs is null)
                {
                    firstVisibleMs = stopwatch.Elapsed.TotalMilliseconds;
                }
            }

            CountOutputCharacters(root, ref visibleCharacters, ref reasoningCharacters);
            finishReason = ReadFinishReason(root) ?? finishReason;

            if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
            {
                promptTokens = ReadInteger(usage, "prompt_tokens") ?? promptTokens;
                completionTokens = ReadInteger(usage, "completion_tokens") ?? completionTokens;
                totalTokens = ReadInteger(usage, "total_tokens") ?? totalTokens;
            }
        }

        double endToEndMs = stopwatch.Elapsed.TotalMilliseconds;
        double firstGenerated = firstGeneratedMs ?? endToEndMs;
        double generationMs = Math.Max(0, endToEndMs - firstGenerated);
        return new ProviderPerformanceSample(
            provider,
            headersMs,
            firstGenerated,
            firstVisibleMs,
            endToEndMs,
            generationMs,
            promptTokens,
            completionTokens,
            totalTokens,
            completionTokens is > 0 && generationMs > 0
                ? completionTokens.Value / (generationMs / 1000d)
                : null,
            finishReason,
            visibleCharacters,
            reasoningCharacters);
    }

    private static bool TryGetDelta(JsonElement root, out bool generated, out bool visible)
    {
        generated = false;
        visible = false;
        if (!root.TryGetProperty("choices", out JsonElement choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return false;
        }

        JsonElement choice = choices[0];
        if (!choice.TryGetProperty("delta", out JsonElement delta) || delta.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        generated = HasText(delta, "content")
            || HasText(delta, "reasoning_content")
            || HasText(delta, "reasoning");
        visible = HasText(delta, "content");
        return generated || visible;
    }

    private static bool HasText(JsonElement value, string property) =>
        value.TryGetProperty(property, out JsonElement text)
        && text.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(text.GetString());

    private static int? ReadInteger(JsonElement value, string property) =>
        value.TryGetProperty(property, out JsonElement number) && number.TryGetInt32(out int parsed)
            ? parsed
            : null;

    private static void CountOutputCharacters(
        JsonElement root,
        ref int visibleCharacters,
        ref int reasoningCharacters)
    {
        if (!root.TryGetProperty("choices", out JsonElement choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0
            || !choices[0].TryGetProperty("delta", out JsonElement delta)
            || delta.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        visibleCharacters += ReadTextLength(delta, "content");
        reasoningCharacters += ReadTextLength(delta, "reasoning_content")
            + ReadTextLength(delta, "reasoning");
    }

    private static string? ReadFinishReason(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out JsonElement choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement choice = choices[0];
        return choice.TryGetProperty("finish_reason", out JsonElement finishReason)
            && finishReason.ValueKind == JsonValueKind.String
                ? finishReason.GetString()
                : null;
    }

    private static int ReadTextLength(JsonElement value, string property) =>
        value.TryGetProperty(property, out JsonElement text)
            && text.ValueKind == JsonValueKind.String
                ? text.GetString()?.Length ?? 0
                : 0;

    private static string FormatMilliseconds(double? value) =>
        value is double milliseconds ? $"{milliseconds:F0}ms" : "n/a";
}

public sealed record ProviderPerformanceSample(
    string Provider,
    double HeadersMs,
    double TimeToFirstGeneratedTokenMs,
    double? TimeToFirstVisibleTokenMs,
    double EndToEndMs,
    double GenerationMs,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    double? ReportedCompletionTokensPerSecond,
    string? FinishReason,
    int VisibleCharacters,
    int ReasoningCharacters);

public sealed record ProviderPerformanceReport(
    string Provider,
    string Model,
    string ReasoningEffort,
    string Endpoint,
    int WarmupRequests,
    int MaxTokens,
    DateTimeOffset MeasuredAtUtc,
    IReadOnlyList<ProviderPerformanceSample> Samples,
    ProviderPerformanceSummary Summary)
{
    public static ProviderPerformanceReport Create(
        string provider,
        string model,
        string reasoningEffort,
        string endpoint,
        int warmupRequests,
        int maxTokens,
        IReadOnlyList<ProviderPerformanceSample> samples) => new(
            provider,
            model,
            reasoningEffort,
            endpoint,
            warmupRequests,
            maxTokens,
            DateTimeOffset.UtcNow,
            samples,
            ProviderPerformanceSummary.Create(samples));
}

public sealed record ProviderPerformanceSummary(
    int Samples,
    double MedianHeadersMs,
    double MedianTimeToFirstGeneratedTokenMs,
    double? MedianTimeToFirstVisibleTokenMs,
    double MedianEndToEndMs,
    double P95EndToEndMs,
    double MedianReportedCompletionTokensPerSecond)
{
    public static ProviderPerformanceSummary Create(IReadOnlyList<ProviderPerformanceSample> samples) => new(
        samples.Count,
        Median(samples.Select(static sample => sample.HeadersMs)),
        Median(samples.Select(static sample => sample.TimeToFirstGeneratedTokenMs)),
        MedianOrNull(samples.Select(static sample => sample.TimeToFirstVisibleTokenMs)),
        Median(samples.Select(static sample => sample.EndToEndMs)),
        Percentile(samples.Select(static sample => sample.EndToEndMs), 0.95),
        Median(samples.Select(static sample => sample.ReportedCompletionTokensPerSecond).OfType<double>()));

    private static double Median(IEnumerable<double> values) => Percentile(values, 0.5);

    private static double? MedianOrNull(IEnumerable<double?> values)
    {
        double[] numeric = values.OfType<double>().Where(double.IsFinite).ToArray();
        return numeric.Length == 0 ? null : Median(numeric);
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] sorted = values.Where(double.IsFinite).Order().ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        double index = (sorted.Length - 1) * percentile;
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);
        return lower == upper
            ? sorted[lower]
            : sorted[lower] + ((sorted[upper] - sorted[lower]) * (index - lower));
    }
}
