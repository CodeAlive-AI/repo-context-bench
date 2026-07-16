# Qwen3.6-35B-A3B Provider Performance Probe

Date: 2026-07-15.

## Scope

This probe compares the OpenAI-compatible streaming endpoints used by CodeAlive
for the same Qwen model family:

- DeepInfra: Qwen/Qwen3.6-35B-A3B, reasoning_effort=high;
- Scaleway: qwen3.6-35b-a3b, reasoning_effort=max.

These are the current production reasoning settings. DeepInfra does not expose
the max effort level, so this is an operational configuration comparison, not
a controlled equal-effort comparison.

The probe sent one fixed 80-token prompt with max_tokens=4000, performed one
warm-up, then collected five sequential streaming samples for each provider.
It records numeric timings and provider-reported token usage only; API keys and
model text are never written to the report.

## Results

Median results:

| Metric | DeepInfra high | Scaleway max |
| --- | ---: | ---: |
| Response headers | 303 ms | 218 ms |
| First generated reasoning token | 484 ms | 253 ms |
| End-to-end | 119.9 s | 41.9 s |
| Completion throughput | 33.5 tokens/s | 96.1 tokens/s |
| End-to-end p95 | 198.9 s | 48.3 s |

For this workload, Scaleway completed the 4000-token reasoning budget about
2.9x faster at the median and streamed about 2.9x more completion tokens per
second. DeepInfra had materially higher tail latency.

## Limitation

All ten measured responses ended with finish_reason=length after consuming
4000 completion tokens. They emitted reasoning text but no visible answer.
Therefore these values measure reasoning throughput and service latency under a
bounded budget, not time to a completed user-visible answer.

Use a larger completion budget and a representative full agent prompt when
measuring end-user completion latency. Keep the providers sequential or
interleave A/B requests from the same host, and report median and p95 instead
of a single sample.

## Reproduction

Build the benchmark project, then run the two probes sequentially:

~~~bash
dotnet run --no-build --project src/RepoContextBench/RepoContextBench.csproj -- perf-probe \
  --provider DeepInfra --model "Qwen/Qwen3.6-35B-A3B" \
  --reasoning-effort high --warmup 1 --samples 5 --max-tokens 4000 \
  --out /path/to/deepinfra.json

dotnet run --no-build --project src/RepoContextBench/RepoContextBench.csproj -- perf-probe \
  --provider Scaleway --model "qwen3.6-35b-a3b" \
  --reasoning-effort max --warmup 1 --samples 5 --max-tokens 4000 \
  --out /path/to/scaleway.json
~~~

perf-probe resolves the locally configured provider credential internally.
Do not pass credentials as command-line arguments or commit probe JSON files
that contain environment-specific endpoint details.
