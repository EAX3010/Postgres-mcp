# Development

## Repository layout

```
├── Models/                          # Configuration and result types
│   ├── AuditEntry.cs
│   ├── DatabaseConfig.cs            # Settings, RiskLevel
│   └── QueryResult.cs
├── Services/
│   ├── AuditLogger.cs               # Append-only JSON log, never throws
│   ├── ConnectionManager.cs         # One NpgsqlDataSource per configured database
│   ├── PostgresService.cs           # Execution, transactions, catalog introspection
│   ├── SafetyGuard.cs               # Classification, risk levels, confirmation policy
│   └── SqlText.cs                   # Comment-aware lexing, quoting, redaction
├── Tools/                           # MCP tool definitions, one class per group
│   ├── AdminTools.cs
│   ├── BackupTools.cs
│   ├── ExecuteTools.cs
│   ├── QueryTools.cs
│   ├── SchemaTools.cs
│   └── ToolJson.cs                  # Shared serialization and response size cap
├── .github/workflows/
│   ├── ci.yml                       # Build, both test suites, CLI smoke test
│   └── release.yml                  # Self-contained archives on a version tag
├── docs/                            # This documentation
├── examples/                        # Ready-to-copy MCP client configs
├── scripts/                         # setup.ps1, setup.sh
├── tests/
│   ├── PostgresMcpServer.Tests/             # Unit; no database
│   └── PostgresMcpServer.IntegrationTests/  # Testcontainers; needs Docker
├── Program.cs                       # Entry point, CLI flags, DI wiring
├── appsettings.example.json         # Reference: every setting
└── CHANGELOG.md
```

### How a tool call flows

```
Tool method (Tools/*.cs)
   │  argument validation, dryRun / confirm handling
   ▼
ISafetyGuard.CheckQuery            → SqlText.Skeleton for comment-aware analysis
   │  operation type, risk level, read-only, rejection
   ▼
IPostgresService                   → READ ONLY transaction for reads,
   │                                  real NpgsqlTransaction for batches
   ▼
IAuditLogger                       → one JSON line, secrets redacted
```

`SqlText` is where the comment- and literal-aware analysis lives. Anything that reasons about
SQL text should go through it rather than matching the raw string.

## Building

```bash
dotnet build "Postgres mcp.sln"
dotnet publish PostgresMcpServer.csproj -c Release -o ./publish
```

The server project sets `TreatWarningsAsErrors`, so a warning fails the build.

The build copies `appsettings.example.json`, `examples/` and `LICENSE` alongside the
executable, so the published output is self-sufficient. `appsettings.json` is never shipped -
`--init` writes it.

## Tests

```bash
dotnet test "Postgres mcp.sln"
```

### Unit tests — no database required

| File | Covers |
|------|--------|
| `SafetyGuardTests.cs` | Statement classification, risk levels, confirmation policy, WHERE detection, the bypasses that once evaded confirmation |
| `SqlTextTests.cs` | Skeletonising, statement counting, identifier and literal quoting, secret redaction, privilege validation |
| `ExampleConfigTests.cs` | The shipped example files: every connection string parses, defaults match the docs, client configs are valid and point at the executable |

`ExampleConfigTests` links the real `appsettings.example.json` and `examples/*.json` into the
test project, so an example that drifts from what the code accepts fails the build rather than
being discovered by a new user.

### Integration tests — need Docker

`SafetyBehaviourTests.cs` starts a throwaway PostgreSQL container via Testcontainers and
asserts what cannot be verified without a server:

- a write is refused inside `query`'s READ ONLY transaction
- `EXPLAIN ANALYZE DELETE` leaves every row in place
- a failing batch rolls back the statements before it
- a successful batch commits, with per-statement row counts
- role passwords are redacted in the audit log
- an audit-log failure does not discard a committed write
- type modifiers, identity columns and constraints survive introspection
- primary keys are not confused by a same-named constraint in another schema
- expression and partial indexes round-trip
- cancellation surfaces as cancellation, not as a failed query

When no Docker daemon is reachable these are **skipped**, not failed — see the `DockerFact`
attribute in `DockerFact.cs`.

### Writing tests

Prefer a unit test against `SafetyGuard` or `SqlText`; they are pure functions and run in
milliseconds. Reach for an integration test only when the behaviour depends on PostgreSQL
actually enforcing something.

## Adding a tool

1. Add the method to the appropriate class in `Tools/`, with `[McpServerTool]` and a
   `[Description]` on the method and on every parameter. Descriptions are what the model sees.
2. Take `database` first and `CancellationToken ct = default` last.
3. Check `_connectionManager.DatabaseExists(database)` before anything else.
4. For reads, call `ExecuteReadOnlyAsync`. For writes, run the statement past `ISafetyGuard`
   and honour `dryRun` and `confirm`.
5. Never interpolate a caller-supplied identifier. Use `SqlText.QuoteIdentifier` or
   `QuoteQualified`; for values, use query parameters.
6. Return `ToolJson.Serialize(...)`, and catch exceptions so failures come back as
   `Error: ...` rather than escaping the tool call. Let `OperationCanceledException` through.
7. Add tests, and update [tools.md](tools.md).

Tools are discovered by reflection through `WithToolsFromAssembly()`; there is no registry to
update.

## Continuous integration

`.github/workflows/ci.yml` builds in Release, runs both suites, and smoke-tests the CLI:
`--help` and `--version` succeed, `--check` fails cleanly with nothing configured, `--check`
succeeds against a service-container PostgreSQL, and stdout carries only JSON-RPC. Docker is
available on GitHub's Ubuntu runners, so the integration tests really run there.

## Releases

`.github/workflows/release.yml` runs on a `v*` tag, or on demand via `workflow_dispatch` if
you want to exercise the build matrix without publishing. It produces self-contained archives
for `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64`, each containing the
executable, `START-HERE.txt`, `appsettings.example.json`, `examples/`, `docs/` and the
licence. The packaged linux build is smoke-tested before upload, and `SHA256SUMS.txt` is
published with the release.

Record user-visible changes in [CHANGELOG.md](../CHANGELOG.md) under `[Unreleased]`, then move
that section under the new version number when you tag.

## Conventions

- Nullable reference types are on; keep them clean.
- Comments explain *why*, particularly where the code guards against something subtle. Do not
  restate what the line does.
- Public service methods take a `CancellationToken` and propagate cancellation rather than
  swallowing it.
- Anything user-facing that can fail should say what to do about it, and get an entry in
  [troubleshooting.md](troubleshooting.md).

## Contributing

1. Fork and create a feature branch
2. Add tests for the behaviour you change
3. `dotnet test "Postgres mcp.sln"`
4. Update the relevant page under `docs/`
5. Open a pull request
