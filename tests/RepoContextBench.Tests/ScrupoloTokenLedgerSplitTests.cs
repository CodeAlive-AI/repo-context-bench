using System.Text.Json;
using AwesomeAssertions;
using RepoContextBench.Ledger;
using CodeAlive.Agents.Codebase.Ledger;

namespace RepoContextBench.Tests;

/// <summary>
/// Verifies the scrupolo main-vs-subagent partitioning in <see cref="RepoContextBenchFileRunLedger"/> /
/// <see cref="TokenLedgerAccumulator"/>: provider cost is split by run hierarchy (ParentRunId is
/// null => main), counts are ledger-derived, and a run with no sub-agent activity keeps the base
/// token-ledger shape (AC6 / AC7).
/// </summary>
public sealed class ScrupoloTokenLedgerSplitTests
{
    private const string TaskId = "task-1";
    private const string RootRunId = "root-run-1";
    private const string ChildRunId = "child-run-1";

    [Fact]
    public async Task RecordAsync_PartitionsProviderTokensByRunHierarchy()
    {
        using TempRunDirectory runDirectory = new();
        RepoContextBenchFileRunLedger ledger = new(runDirectory.Path, "warn");

        // Main (Scrupolo manager) model call: ParentRunId == null.
        await ledger.RecordAsync(
            ModelCallCompleted(parentRunId: null, rootRunId: RootRunId, inputTokens: 1000, outputTokens: 200),
            CancellationToken.None);
        // Two sub-agent (`ask`) model calls: ParentRunId == root.
        await ledger.RecordAsync(
            ModelCallCompleted(parentRunId: RootRunId, rootRunId: RootRunId, inputTokens: 300, outputTokens: 50),
            CancellationToken.None);
        await ledger.RecordAsync(
            ModelCallCompleted(parentRunId: RootRunId, rootRunId: RootRunId, inputTokens: 400, outputTokens: 70),
            CancellationToken.None);
        // One sub-agent tool call (only sub-agent tools reach the ledger).
        await ledger.RecordAsync(
            ToolCallCompleted(parentRunId: RootRunId, rootRunId: RootRunId, toolName: "semantic_search"),
            CancellationToken.None);

        TokenLedgerAccumulator? accumulator = ledger.GetTaskAccumulator(TaskId);

        accumulator.Should().NotBeNull();
        accumulator!.MainProviderInputTokens.Should().Be(1000);
        accumulator.MainProviderOutputTokens.Should().Be(200);
        accumulator.SubProviderInputTokens.Should().Be(700);
        accumulator.SubProviderOutputTokens.Should().Be(120);
        accumulator.MainModelCalls.Should().Be(1);
        accumulator.SubModelCalls.Should().Be(2);
        accumulator.SubToolCalls.Should().Be(1);
        accumulator.HasSubAgentActivity.Should().BeTrue();
        accumulator.ToolCallsByName.Should().ContainKey("semantic_search");

        // Aggregate provider totals remain the full run total (main + sub), non-overlapping (AC6).
        accumulator.ProviderInputTokens.Should().Be(1700);
        accumulator.ProviderOutputTokens.Should().Be(320);
    }

    [Fact]
    public async Task RecordAsync_NonScrupoloRun_LeavesSplitEmpty()
    {
        using TempRunDirectory runDirectory = new();
        RepoContextBenchFileRunLedger ledger = new(runDirectory.Path, "warn");

        // A flat (non-scrupolo) run: every event has ParentRunId == null.
        await ledger.RecordAsync(
            ModelCallCompleted(parentRunId: null, rootRunId: null, inputTokens: 500, outputTokens: 100),
            CancellationToken.None);

        TokenLedgerAccumulator? accumulator = ledger.GetTaskAccumulator(TaskId);

        accumulator.Should().NotBeNull();
        accumulator!.HasSubAgentActivity.Should().BeFalse();
        accumulator.SubProviderInputTokens.Should().BeNull();
        accumulator.SubProviderOutputTokens.Should().BeNull();
        accumulator.SubModelCalls.Should().Be(0);
        accumulator.SubToolCalls.Should().Be(0);
        // Main tokens are still attributed (so the cost split is well-defined when scrupolo is on),
        // but the run-level token_ledger.json stays on the base shape because there is no sub activity.
        accumulator.MainProviderInputTokens.Should().Be(500);
    }

    [Fact]
    public async Task RecordAsync_PopulatesLocalSplit_WhenProviderUsageAbsent()
    {
        // Scaleway streaming returns no provider usage, so provider_usage is absent and the Provider*
        // fields stay null. The local (tiktoken) split MUST still partition tokens main vs sub by run
        // hierarchy so the per-task score can fall back to it (the main-agent token tracking the user asked for).
        using TempRunDirectory runDirectory = new();
        RepoContextBenchFileRunLedger ledger = new(runDirectory.Path, "warn");

        // Main manager call: input(started)=900, output(completed)=120, NO provider_usage.
        await ledger.RecordAsync(ModelCallLocal(ContextResearchLedgerEventTypes.ModelCallStarted, parentRunId: null, localTokens: 900), CancellationToken.None);
        await ledger.RecordAsync(ModelCallLocal(ContextResearchLedgerEventTypes.ModelCallCompleted, parentRunId: null, localTokens: 120), CancellationToken.None);
        // Two sub-agent (`ask`) calls: input 5000 + 3000, output 200 + 150.
        await ledger.RecordAsync(ModelCallLocal(ContextResearchLedgerEventTypes.ModelCallStarted, parentRunId: RootRunId, localTokens: 5000), CancellationToken.None);
        await ledger.RecordAsync(ModelCallLocal(ContextResearchLedgerEventTypes.ModelCallCompleted, parentRunId: RootRunId, localTokens: 200), CancellationToken.None);
        await ledger.RecordAsync(ModelCallLocal(ContextResearchLedgerEventTypes.ModelCallStarted, parentRunId: RootRunId, localTokens: 3000), CancellationToken.None);
        await ledger.RecordAsync(ModelCallLocal(ContextResearchLedgerEventTypes.ModelCallCompleted, parentRunId: RootRunId, localTokens: 150), CancellationToken.None);

        TokenLedgerAccumulator? acc = ledger.GetTaskAccumulator(TaskId);

        acc.Should().NotBeNull();
        // Provider split is null — the provider reported no usage.
        acc!.MainProviderInputTokens.Should().BeNull();
        acc.SubProviderInputTokens.Should().BeNull();
        // Local split is populated and partitioned by run hierarchy (the fallback the score uses).
        acc.MainLocalModelInputTokens.Should().Be(900);
        acc.MainLocalModelOutputTokens.Should().Be(120);
        acc.SubLocalModelInputTokens.Should().Be(8000);
        acc.SubLocalModelOutputTokens.Should().Be(350);
        acc.MainModelCalls.Should().Be(1);
        acc.SubModelCalls.Should().Be(2);
    }

    [Fact]
    public void RecordMainToolCalls_MergesMainAgentToolCountsIntoBreakdown()
    {
        using TempRunDirectory runDirectory = new();
        RepoContextBenchFileRunLedger ledger = new(runDirectory.Path, "warn");

        ledger.RecordMainToolCalls(TaskId, new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["ask"] = 3,
            ["read_file"] = 2,
            ["get_ontology"] = 1,
        });

        TokenLedgerAccumulator? accumulator = ledger.GetTaskAccumulator(TaskId);

        accumulator.Should().NotBeNull();
        accumulator!.ToolCallsByName.Should().NotBeNull();
        accumulator.ToolCallsByName!["ask"].Should().Be(3);
        accumulator.ToolCallsByName["read_file"].Should().Be(2);
        accumulator.ToolCallsByName["get_ontology"].Should().Be(1);
    }

    [Fact]
    public void Add_MergesToolCallsByNameAcrossTasks()
    {
        // Run-level token_ledger.json tool_calls_by_name is the sum across per-task accumulators.
        TokenLedgerAccumulator total = new();
        TokenLedgerAccumulator taskA = new();
        taskA.AddToolCall("ask", 3);
        taskA.AddToolCall("semantic_search", 2);
        TokenLedgerAccumulator taskB = new();
        taskB.AddToolCall("ask", 1);
        taskB.AddToolCall("read_file", 4);

        total.Add(taskA);
        total.Add(taskB);

        total.ToolCallsByName.Should().NotBeNull();
        total.ToolCallsByName!["ask"].Should().Be(4);
        total.ToolCallsByName["semantic_search"].Should().Be(2);
        total.ToolCallsByName["read_file"].Should().Be(4);
    }

    [Fact]
    public void Serialize_NonScrupoloAccumulator_OmitsScrupoloSplitFields()
    {
        // AC7: a non-scrupolo per-task accumulator (all scrupolo fields default) must serialize
        // byte-identically to before — the additive split fields carry [JsonIgnore(WhenWritingDefault)]
        // and must not appear, while the existing provider_* nulls remain.
        // Simulate a REAL non-scrupolo run: every model call is "main" (ParentRunId == null), so
        // MainModelCalls and the local-main split are NON-ZERO — exactly the state that, with the old
        // [JsonIgnore(WhenWritingDefault)], would have leaked main_model_calls into the per-task
        // token_ledger.json and broken AC7. They must be omitted because the split fields are
        // [JsonIgnore] (always).
        TokenLedgerAccumulator accumulator = new()
        {
            ProviderInputTokens = 100,
            ProviderOutputTokens = 20,
            MainModelCalls = 5,
            MainLocalModelInputTokens = 1000,
            MainLocalModelOutputTokens = 200,
        };

        string json = JsonSerializer.Serialize(accumulator, ArtifactJsonOptions);

        // snake_case names mirror RepoContextBenchArtifactWriter (the real token_ledger.json serializer).
        json.Should().NotContain("main_provider_input_tokens");
        json.Should().NotContain("main_provider_output_tokens");
        json.Should().NotContain("sub_provider_input_tokens");
        json.Should().NotContain("sub_provider_output_tokens");
        json.Should().NotContain("main_model_calls");
        json.Should().NotContain("sub_model_calls");
        json.Should().NotContain("sub_tool_calls");
        json.Should().NotContain("has_sub_agent_activity");
        json.Should().NotContain("main_local_model_input_tokens");
        json.Should().NotContain("main_local_model_output_tokens");
        json.Should().NotContain("tool_calls_by_name");
        // Existing fields are still present (provider totals + zeroed local counts).
        json.Should().Contain("provider_input_tokens");
        json.Should().Contain("local_model_input_tokens");
    }

    [Fact]
    public void Serialize_ScrupoloAccumulator_IncludesSplitFields()
    {
        TokenLedgerAccumulator accumulator = new()
        {
            MainProviderInputTokens = 1000,
            SubProviderInputTokens = 700,
            MainModelCalls = 1,
            SubModelCalls = 2,
            SubToolCalls = 1,
        };
        accumulator.AddToolCall("ask", 3);

        string json = JsonSerializer.Serialize(accumulator, ArtifactJsonOptions);

        json.Should().Contain("main_provider_input_tokens");
        json.Should().Contain("sub_provider_input_tokens");
        json.Should().Contain("tool_calls_by_name");
    }

    // Mirrors RepoContextBenchArtifactWriter's options so the per-task accumulator serialization under test
    // matches the real token_ledger.json key casing.
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static ContextResearchLedgerEvent ModelCallCompleted(
        string? parentRunId,
        string? rootRunId,
        long inputTokens,
        long outputTokens)
    {
        ContextResearchLedgerEvent evt = ContextResearchLedgerEvent.Create(
            ContextResearchLedgerEventTypes.ModelCallCompleted,
            Identity(parentRunId, rootRunId));
        evt.Data["provider_usage"] = new
        {
            input_tokens = inputTokens,
            output_tokens = outputTokens,
        };
        return evt;
    }

    private static ContextResearchLedgerEvent ToolCallCompleted(
        string? parentRunId,
        string? rootRunId,
        string toolName)
    {
        ContextResearchLedgerEvent evt = ContextResearchLedgerEvent.Create(
            ContextResearchLedgerEventTypes.ToolCallCompleted,
            Identity(parentRunId, rootRunId));
        evt.ToolName = toolName;
        return evt;
    }

    private static ContextResearchLedgerEvent ModelCallLocal(string eventType, string? parentRunId, long localTokens)
    {
        // No provider_usage -> Provider* stays null; PayloadTokensLocal is the only token signal,
        // exactly as a Scaleway model call records it.
        ContextResearchLedgerEvent evt = ContextResearchLedgerEvent.Create(eventType, Identity(parentRunId, RootRunId));
        evt.PayloadTokensLocal = localTokens;
        return evt;
    }

    private static ContextResearchRunIdentity Identity(string? parentRunId, string? rootRunId) => new(
        AgentRunId: parentRunId is null ? RootRunId : ChildRunId,
        ExternalRunId: "run-out",
        TaskId: TaskId,
        ConversationId: "conv-1",
        DataSourceIds: ["ds-1"],
        ParentRunId: parentRunId,
        RootRunId: rootRunId);

    private sealed class TempRunDirectory : IDisposable
    {
        public TempRunDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "scrupolo-ledger-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup; a leaked temp directory must not fail the test.
            }
        }
    }
}
