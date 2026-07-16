// @vitest-environment jsdom

import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { __testables, App } from "./main";

const runsPayload = {
  runs: [
    run("run-a", 2, 0.6, "local_repository", "disabled", {
      modelInputTokens: 100,
      modelOutputTokens: 20,
      toolRawOutputTokens: 40,
      toolInsertedTokens: 20,
    }),
    run("run-b", 2, 0.65, "local_repository", "enabled", {
      modelInputTokens: 80,
      modelOutputTokens: 20,
      toolRawOutputTokens: 20,
      toolInsertedTokens: 10,
    }),
    run("run-c", 2, 0.8, "native_agent_tools", "enabled", {
      modelInputTokens: 70,
      modelOutputTokens: 20,
      toolRawOutputTokens: 20,
      toolInsertedTokens: 10,
    }),
  ],
};

const taskIndexPayload = {
  tasks: [
    {
      taskId: "task-1",
      taskFile: "task-1.json",
      repo: "microsoft/agent-framework",
      questionType: "control_data_flow",
      answerability: "answerable_static",
      expectedBehavior: "answer",
      question: "How does the workflow combine agents?",
      runs: [
        taskRun("run-a", "Answer from run A", 0.6),
        taskRun("run-b", "Answer from run B", 0.65),
        taskRun("run-c", "Answer from run C", 0.8),
      ],
    },
  ],
};

const taskDetailPayload = {
  taskId: "task-1",
  task: {
    question: "How does the workflow combine agents?",
    question_type: "control_data_flow",
    answerability: "answerable_static",
    expected_behavior: "answer",
    gold_answer: "Specialist agents run in parallel and aggregate responses.",
    gold_claims: [{ id: "F1", importance: "required", text: "Specialist agents run in parallel.", evidence: ["src/workflow.cs"] }],
  },
  runs: [
    taskRun("run-a", "Answer from run A", 0.6),
    taskRun("run-b", "Answer from run B", 0.65),
    taskRun("run-c", "Answer from run C", 0.8),
  ],
};

describe("RepoContextBench report app", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input);
      const body = responseFor(path);
      if (!body) {
        return new Response("not found", { status: 404 });
      }

      return Response.json(body);
    }));
  });

  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  it("renders the simplified navigation and overview objectives", async () => {
    render(<App />);

    await screen.findByRole("heading", { name: "Model leaderboard: quality / speed / price" });
    const navigation = within(screen.getByLabelText("Report navigation"));
    expect(navigation.getByRole("button", { name: "Overview" })).toBeTruthy();
    expect(navigation.getByRole("button", { name: "Runs" })).toBeTruthy();
    expect(navigation.getByRole("button", { name: "Compare" })).toBeTruthy();
    expect(navigation.getByRole("button", { name: "Tasks" })).toBeTruthy();
    expect(navigation.getByRole("button", { name: "How it works" })).toBeTruthy();
    expect(navigation.queryByRole("button", { name: "Dataset" })).toBeNull();
    expect(navigation.queryByRole("button", { name: "Chat" })).toBeNull();
    expect(navigation.queryByRole("button", { name: "Token diff" })).toBeNull();
    expect(document.body.textContent).toContain("CodeAlive impact: quality, cost, time");
    expect(document.body.textContent).toContain("Semantic search impact: savings and quality tradeoff");
    expect(document.body.textContent).toContain("semantic_search saves");
    expect(document.body.textContent).toContain("Quality × Cost");
    expect(document.body.textContent).toContain("Pareto frontier");
  });

  it("explains the benchmark with a live task example and claim verdicts", async () => {
    render(<App />);
    const user = userEvent.setup();

    await screen.findByRole("heading", { name: "Model leaderboard: quality / speed / price" });
    await user.click(within(screen.getByLabelText("Report navigation")).getByRole("button", { name: "How it works" }));

    await screen.findByRole("heading", { name: "How the benchmark works" });
    await screen.findByText("Repository question");
    await screen.findByRole("heading", { name: "Anatomy of one task" });
    await screen.findByText("Gold claims checklist");
    await screen.findByText("F1 · required");
    await screen.findByText("partial");
    await screen.findByText("How to read Overview metrics");
    await screen.findByText("Question types");
  });

  it("builds the quality cost map from cost per task and groups lines by model family plus harness", () => {
    const rows = [
      ...runsPayload.runs,
      {
        ...runsPayload.runs[0],
        id: "run-d",
        costSummary: { totalCostUsd: 1.2, costSource: "provider_reported" },
        executionProfile: {
          ...runsPayload.runs[0].executionProfile,
          harness: "codex_cli",
        },
      },
    ].map(__testables.toRunMatrixRow);

    const chart = __testables.buildValueMapSeries(rows, "linear");
    const pointA = chart.points.find((point) => point.id === "run-a");
    const pointB = chart.points.find((point) => point.id === "run-b");
    const pointD = chart.points.find((point) => point.id === "run-d");

    expect(pointA?.costPerTask).toBeCloseTo(0.15);
    expect(pointB?.costPerTask).toBeCloseTo(0.1);
    expect(pointD?.costPerTask).toBeCloseTo(0.6);
    expect(chart.series.map((series) => series.key).sort()).toEqual([
      "Gemini 3.5 Flash|codealive_context_research_agent",
      "Gemini 3.5 Flash|codex_cli",
    ]);
  });

  it("formats compare run options by model name, sorts by model, and marks no-semantic runs", () => {
    const options = __testables.buildCompareRunOptions([
      {
        ...run("run-z", 2, 0.7, "local_repository", "enabled", {}),
        answerer: { provider: "Test", model: "zzz-model", reasoningEffort: "high" },
      },
      {
        ...run("run-a", 2, 0.7, "local_repository", "disabled", {}),
        answerer: { provider: "Test", model: "aaa-model", reasoningEffort: "high" },
      },
    ]);

    const labels = options.map((option) => option.label);
    expect(labels[0]).toMatch(/^aaa-model high \[NO SEMANTIC\]/);
    expect(labels[1]).toMatch(/^zzz-model high \[semantic\]/);
    expect(labels.every((label) => !label.startsWith("run-"))).toBe(true);
  });

  it("opens task details, switches run tabs, shows chat replay, and closes the drawer", async () => {
    render(<App />);
    const user = userEvent.setup();

    await screen.findByRole("heading", { name: "Model leaderboard: quality / speed / price" });
    await user.click(within(screen.getByLabelText("Report navigation")).getByRole("button", { name: "Tasks" }));
    await screen.findByRole("heading", { name: "Tasks: request → answer → evidence" });
    await user.click(screen.getAllByRole("button", { name: /task-1/i })[0]);

    await screen.findByRole("region", { name: "Request and model answer" });
    expect(await screen.findAllByText("Answer from run A")).toHaveLength(2);
    const strongRun = screen.getAllByRole("button").find((button) => button.textContent?.includes("80%"));
    expect(strongRun?.textContent).toContain("pass");
    await user.click(strongRun!);

    expect(await screen.findAllByText("Answer from run C")).toHaveLength(2);
    await screen.findByLabelText("Benchmark chat transcript");
    expect(document.body.textContent).toContain("semantic_search");
    expect(document.body.textContent).toContain("payloads/tool/semantic_search.json");

    await user.click(screen.getByRole("button", { name: "Close task details" }));
    await waitFor(() => expect(screen.queryByText("Answer from run C")).toBeNull());
  });

  it("filters task rows by search and quality issues", async () => {
    render(<App />);
    const user = userEvent.setup();

    await screen.findByRole("heading", { name: "Model leaderboard: quality / speed / price" });
    await user.click(within(screen.getByLabelText("Report navigation")).getByRole("button", { name: "Tasks" }));
    await user.type(screen.getByPlaceholderText("task, repo, question"), "workflow");
    expect(await screen.findAllByText("How does the workflow combine agents?")).toHaveLength(3);

    await user.click(screen.getByRole("button", { name: "Quality issues" }));
    await waitFor(() => expect(document.body.textContent).toContain("0 row(s)"));
  });

  it("merges compare and token diff with total and average per evaluated task", async () => {
    render(<App />);
    const user = userEvent.setup();

    await screen.findByRole("heading", { name: "Model leaderboard: quality / speed / price" });
    await user.click(within(screen.getByLabelText("Report navigation")).getByRole("button", { name: "Compare" }));

    await screen.findByText("Answerer model tokens");
    const runASelect = screen.getByLabelText("Run A baseline") as HTMLSelectElement;
    const runBSelect = screen.getByLabelText("Run B candidate") as HTMLSelectElement;
    const initialRunA = runASelect.value;
    const initialRunB = runBSelect.value;
    const runAOptions = Array.from(runASelect.options).map((option) => option.textContent ?? "");
    expect(runAOptions[0]).toMatch(/^Gemini 3\.5 Flash/);
    expect(runAOptions.some((option) => option.includes("[NO SEMANTIC]"))).toBe(true);
    await user.click(screen.getByRole("button", { name: "Swap Run A and Run B" }));
    expect(runASelect.value).toBe(initialRunB);
    expect(runBSelect.value).toBe(initialRunA);
    expect(document.body.textContent).toContain("120");
    expect(document.body.textContent).not.toContain("Judge tokens");

    await user.click(screen.getByRole("button", { name: "Average / evaluated task" }));
    await waitFor(() => expect(document.body.textContent).toContain("60"));
  });

  it("keeps the Runs page intact with answerer-only time", async () => {
    render(<App />);
    const user = userEvent.setup();

    await screen.findByRole("heading", { name: "Model leaderboard: quality / speed / price" });
    await user.click(within(screen.getByLabelText("Report navigation")).getByRole("button", { name: "Runs" }));

    await screen.findByRole("heading", { name: "Compact run comparison" });
    expect(document.body.textContent).toContain("Answerer time");
    expect(document.body.textContent).toContain("2 min");
    expect(document.body.textContent).not.toContain("16.67 min");
  });
});

function responseFor(path: string): unknown {
  if (path.endsWith("/data/runs.json")) return runsPayload;
  if (path.endsWith("/data/leaderboard.json")) return { rows: runsPayload.runs };
  if (path.endsWith("/data/tasks_index.json")) return taskIndexPayload;
  if (path.endsWith("/data/tasks/task-1.json")) return taskDetailPayload;
  if (path.endsWith("/data/run_diffs/run-a__run-b.json")) {
    return { newlyPassed: 0, newlyFailed: 1, rows: [] };
  }

  return null;
}

function run(id: string, evaluatedTaskCount: number, quality: number, codeAliveContext: string, semanticSearch: string, tokenUsage: Record<string, number>) {
  return {
    id,
    track: "static_qa",
    datasetName: "repo_context_bench_seed",
    benchmarkVersion: "repo_context_bench_v3",
    repositoryName: "microsoft/agent-framework",
    repositoryId: "repo-id",
    evaluatedTaskCount,
    scoredTaskCount: evaluatedTaskCount,
    datasetTaskCount: 2,
    score: {
      taskCount: evaluatedTaskCount,
      scoredTaskCount: evaluatedTaskCount,
      judgeQualityScore: quality,
      scoredStrictGoldPassRate: quality > 0.7 ? 0.5 : 0,
      certificationDegradeTaskCount: 1,
      certificationBlockTaskCount: 0,
      networkFailureTaskCount: 0,
      networkFailureRate: 0,
      judgeRequiredClaimRecall: quality,
      evidenceUseScore: quality,
      judgeFaithfulnessScore: quality,
      runHealthStatus: "reportable",
      qualityReportable: true,
    },
    tokenUsage,
    costSummary: {
      totalCostUsd: id === "run-a" ? 0.3 : id === "run-b" ? 0.2 : 0.22,
      costSource: "provider_reported",
    },
    executionProfile: {
      harness: "codealive_context_research_agent",
      answererMode: "standard",
      researchMode: "standard",
      codeAliveContext,
      semanticSearch,
      ontologyContext: "enabled",
      subagentPolicy: "not_requested",
      maxTurns: 500,
    },
    runTiming: {
      startedAtUtc: "2026-06-08T10:00:00Z",
      finishedAtUtc: "2026-06-08T10:20:00Z",
      wallTimeMs: 999999,
      taskCount: evaluatedTaskCount,
      scoredTaskCount: evaluatedTaskCount,
      sumTaskWallTimeMs: 120000,
      averageTaskWallTimeMs: 60000,
      maxParallel: 1,
    },
    judgeTokenLedger: {
      provider: "Gemini",
      model: "gemini-3.5-flash",
      reasoningEffort: "high",
      modelInputTokens: 10,
      modelOutputTokens: 10,
    },
    answerer: {
      provider: "Gemini",
      model: "gemini-3.5-flash",
      reasoningEffort: "medium",
    },
    toolUsage: {
      totalCalls: 2,
      averageCallsPerTask: 1,
      tools: [{ toolName: "semantic_search", calls: semanticSearch === "enabled" ? 1 : 0, share: semanticSearch === "enabled" ? 0.5 : 0, outputTokens: semanticSearch === "enabled" ? 100 : 0 }],
    },
  };
}

function taskRun(runId: string, rawAnswer: string, quality: number) {
  return {
    runId,
    state: "scored",
    passed: quality > 0.7,
    certificationGate: quality > 0.7 ? "pass" : "degrade",
    certificationGateReason: "fixture",
    answerabilityAccurate: true,
    fileRecall: quality,
    claimRecall: quality,
    evidenceUseScore: quality,
    judgeQualityScore: quality,
    judgeFaithfulnessScore: quality,
    judgeHarmfulFindingRate: 0,
    judgeUnsupportedFindingRate: 0,
    judgeOffScopeFindingRate: 0,
    judgeUnverifiableFindingRate: 0,
    judgeStatus: "success",
    wallTimeMs: 1000,
    toolCalls: 2,
    modelCalls: 3,
    tokenUsage: {
      modelInputTokens: runId === "run-a" ? 100 : runId === "run-b" ? 80 : 70,
      modelOutputTokens: 20,
      toolRawOutputTokens: runId === "run-a" ? 40 : 20,
      toolInsertedTokens: runId === "run-a" ? 20 : 10,
    },
    toolUsage: {
      totalCalls: 2,
      tools: [{ toolName: "semantic_search", calls: 1, share: 0.5, outputTokens: 100 }],
    },
    toolTrace: [
      {
        eventType: "tool_call_started",
        sequence: 1,
        toolName: "semantic_search",
        status: "started",
        argsJson: "{\"query\":\"workflow agents\"}",
        argsTokensLocal: 12,
      },
      {
        eventType: "tool_call_completed",
        sequence: 2,
        toolName: "semantic_search",
        status: "success",
        latencyMs: 180,
        payloadTokensLocal: 100,
        returnPayloadTokensLocal: 100,
        payloadRef: "payloads/tool/semantic_search.json",
      },
    ],
    trace: {
      rawAnswer,
      retrievedContext: [{ path: "src/workflow.cs", startLine: 1, endLine: 10, rank: 1, toolName: "semantic_search" }],
    },
    judge: {
      rationale: "fixture",
      gold_claim_coverage: [
        {
          gold_claim_id: "F1",
          status: quality > 0.7 ? "covered" : "partially_covered",
          confidence: 0.9,
          rationale: quality > 0.7 ? "covered in answer" : "answer only partially covers the claim",
        },
      ],
      quality: { score: quality, passed: quality > 0.7 },
      faithfulness: { score: quality },
      evidenceUse: { score: quality },
      answerability: { correct: true },
    },
    isNetworkFailure: false,
  };
}
