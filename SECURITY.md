# Security

Do not commit provider keys, ChatGPT/Codex credentials, CodeAlive credentials,
private repository content, local database identifiers, or absolute workstation
paths. Use environment variables, .NET user-secrets, or the provider's supported
local authentication flow.

Before publishing results, use `export-publication`; never copy private run
directories directly. The exporter validates run health, rewrites internal IDs
and local paths, scans common secret formats, and records source/published hashes.

Report a suspected exposure privately to `security@codealive.ai`. Do not open a
public issue containing credentials or private source code.
