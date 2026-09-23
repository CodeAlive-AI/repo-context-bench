# Security

Published artifacts must never contain provider keys, credentials, private
repository content, internal identifiers, or absolute workstation paths. Results
enter this repository only through the benchmark's publication exporter, which
validates run health, rewrites internal IDs and local paths, scans common secret
formats, and records source and published hashes.

Report a suspected exposure privately to `security@codealive.ai`. Do not open a
public issue containing credentials or private source code.
