# RepoContextBench repository guide

> Before editing or inspecting a target path, read every applicable `AGENTS.md` from the repository root through the target directory. Do not load instructions from unrelated subtrees.

This public repository carries the benchmark's methodology, the published v1 dataset
and results, and the dashboard. It contains no runner source; the v1 runner is
preserved in the `v1.0.0` tag.

## Invariants

- v1 is immutable: never rewrite `dataset/agent-framework/v1`, `results/v1`, or an
  existing release tag.
- `docs/v2/methodology.md` is public-facing: keep it free of internal product names,
  operator flags, credentials handling, internal hostnames, local paths, and
  unpublished experiment figures.
- Published result artifacts are produced only by the benchmark's publication
  exporter; never hand-edit or copy raw run directories into `results/`.
- Before pushing, search the repository for absolute paths, internal ObjectIds,
  provider secrets, private hostnames, and the former `RepoQA` name.
- Tag releases as `vMAJOR.MINOR.PATCH`; never move an existing tag.
