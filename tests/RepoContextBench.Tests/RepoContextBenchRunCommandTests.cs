using AwesomeAssertions;
using RepoContextBench.Running;
using MongoDB.Bson;

namespace RepoContextBench.Tests;

public sealed class RepoContextBenchRunCommandTests
{
    [Fact]
    public void Parse_JudgeRegression_DoesNotRequireOrganisationOrRepository()
    {
        RepoContextBenchRunCommand command = RepoContextBenchRunCommand.Parse([
            "judge-regression",
            "--dataset",
            "tasks.jsonl",
            "--judge-regression-file",
            "cases.jsonl",
            "--out",
            "out-dir",
            "--judge-provider",
            "codex_cli",
            "--judge-model",
            "gpt-5.5",
            "--judge-reasoning-effort",
            "high",
        ]);

        command.CommandName.Should().Be("judge-regression");
        command.Dataset.Should().Be("tasks.jsonl");
        command.JudgeRegressionFile.Should().Be("cases.jsonl");
        command.Out.Should().Be("out-dir");
        command.Judge.Should().Be("enabled");
        command.JudgeProvider.Should().Be("codex_cli");
        command.JudgeModel.Should().Be("gpt-5.5");
        command.JudgeReasoningEffort.Should().Be("high");
        command.OrganisationId.Should().Be(ObjectId.Empty);
        command.RepositoryId.Should().BeNull();
        command.WorkspaceId.Should().BeNull();
    }

    [Fact]
    public void Parse_JudgeRegression_DefaultsToEnabledCodexJudge()
    {
        RepoContextBenchRunCommand command = RepoContextBenchRunCommand.Parse([
            "judge-regression",
            "--dataset",
            "tasks.jsonl",
            "--regression-file",
            "cases.jsonl",
            "--out",
            "out-dir",
        ]);

        command.Judge.Should().Be("enabled");
        command.JudgeProvider.Should().Be("codex_cli");
        command.JudgeModel.Should().Be("gpt-5.5");
        command.JudgeReasoningEffort.Should().Be("high");
        command.JudgeRegressionFile.Should().Be("cases.jsonl");
    }

    [Fact]
    public void Parse_Run_PreservesAnswererModelOverrides()
    {
        RepoContextBenchRunCommand command = RepoContextBenchRunCommand.Parse([
            "run",
            "--dataset",
            "tasks.jsonl",
            "--repository-id",
            "repo-id",
            "--organisation-id",
            ObjectId.GenerateNewId().ToString(),
            "--out",
            "out-dir",
            "--answerer-provider",
            "Scaleway",
            "--answerer-model",
            "qwen3.5-397b-a17b",
            "--answerer-reasoning-effort",
            "max",
        ]);

        command.AnswererProviderOverride.Should().Be("Scaleway");
        command.AnswererModelOverride.Should().Be("qwen3.5-397b-a17b");
        command.AnswererReasoningEffortOverride.Should().Be("max");
    }

    [Fact]
    public void Parse_Run_RecognisesScrupoloAnswererAndOverrides()
    {
        RepoContextBenchRunCommand command = RepoContextBenchRunCommand.Parse([
            "run",
            "--dataset",
            "tasks.jsonl",
            "--repository-id",
            "repo-id",
            "--organisation-id",
            ObjectId.GenerateNewId().ToString(),
            "--out",
            "out-dir",
            "--answerer",
            "scrupolo",
            "--scrupolo-provider",
            "Scaleway",
            "--scrupolo-model",
            "qwen3.5-397b-a17b",
            "--scrupolo-reasoning-effort",
            "max",
        ]);

        command.Answerer.Should().Be("scrupolo");
        command.UsesScrupoloAnswerer.Should().BeTrue();
        command.UsesExternalCliAnswerer.Should().BeFalse();
        command.ScrupoloProviderOverride.Should().Be("Scaleway");
        command.ScrupoloModelOverride.Should().Be("qwen3.5-397b-a17b");
        command.ScrupoloReasoningEffortOverride.Should().Be("max");
        // --scrupolo-* must NOT leak into the ContextResearchAgent answerer overrides (Codex MED-10).
        command.AnswererModelOverride.Should().BeNull();
    }

    [Fact]
    public void Parse_Run_ScrupoloTrackTriggersScrupoloAnswerer()
    {
        RepoContextBenchRunCommand command = RepoContextBenchRunCommand.Parse([
            "run",
            "--dataset",
            "tasks.jsonl",
            "--repository-id",
            "repo-id",
            "--organisation-id",
            ObjectId.GenerateNewId().ToString(),
            "--out",
            "out-dir",
            "--track",
            "scrupolo",
        ]);

        command.UsesScrupoloAnswerer.Should().BeTrue();
    }

    [Fact]
    public void Parse_Run_DefaultsLeaveScrupoloDisabled()
    {
        RepoContextBenchRunCommand command = RepoContextBenchRunCommand.Parse([
            "run",
            "--dataset",
            "tasks.jsonl",
            "--repository-id",
            "repo-id",
            "--organisation-id",
            ObjectId.GenerateNewId().ToString(),
            "--out",
            "out-dir",
        ]);

        command.UsesScrupoloAnswerer.Should().BeFalse();
        command.ScrupoloProviderOverride.Should().BeNull();
        command.ScrupoloModelOverride.Should().BeNull();
        command.ScrupoloReasoningEffortOverride.Should().BeNull();
    }

    [Fact]
    public void Parse_ValidateDataset_PreservesSourceRoot()
    {
        RepoContextBenchRunCommand command = RepoContextBenchRunCommand.Parse([
            "validate-dataset",
            "--dataset",
            "tasks.jsonl",
            "--manifest",
            "manifest.json",
            "--source-root",
            "/tmp/repo",
        ]);

        command.CommandName.Should().Be("validate-dataset");
        command.SourceRoot.Should().Be("/tmp/repo");
    }
}
