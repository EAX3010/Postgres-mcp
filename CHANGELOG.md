# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project uses
[semantic versioning](https://semver.org/).

## [Unreleased]

### Security

- **stdout no longer carries log output.** The host installs a console logger targeting
  stdout, which is the MCP JSON-RPC channel, so every log record corrupted the protocol from
  the first byte. All logging now goes to stderr.
- **Read-only is enforced by the database.** `query` runs inside a `READ ONLY` transaction
  that is always rolled back, so a write is refused by PostgreSQL even when the statement was
  misclassified.
- **Multi-statement input is refused.** `SELECT 1; DROP TABLE users; --` was classified by its
  first keyword and then executed in full.
- **`EXPLAIN ANALYZE` no longer executes unguarded.** It now runs inside a rolled-back
  transaction and requires confirmation for a writing statement.
- **Role passwords no longer reach the audit log.** They are quoted as literals, and
  `PASSWORD` literals are redacted before anything is written.
- **Identifiers are quoted rather than interpolated**, so a crafted table name can no longer
  run something other than what was confirmed.

### Added

- `--init` creates a starter `appsettings.json` next to the executable, so a downloaded
  release needs no repository.
- `--check` validates the configuration and connects to every database, exiting non-zero on
  failure.
- `--help` and `--version`.
- Configuration via `POSTGRESMCP_`-prefixed environment variables; `appsettings.json` is now
  optional.
- `Safety.ConfirmAtRiskLevel`, `Safety.AllowMultiStatement`, and the `Limits` section
  (`CommandTimeoutSeconds`, `MaxRows`, `MaxResponseBytes`).
- Unit test suite: statement classification, risk levels, quoting, redaction, and validation
  of the shipped example files.
- Integration test suite using Testcontainers, asserting read-only enforcement,
  `EXPLAIN ANALYZE` rollback, transactional batches, audit redaction and catalog
  introspection. Skipped, not failed, when Docker is unavailable.
- `docs/` covering installation, configuration, tools, safety, security, troubleshooting and
  development, with a page per MCP client.
- `examples/` with ready-to-copy configuration for Claude Desktop, Claude Code, Cursor,
  VS Code, Windsurf, Cline, Gemini CLI and Zed.
- `scripts/setup.ps1` and `scripts/setup.sh`.
- CI and release workflows; releases ship self-contained per-platform archives.

### Fixed

- `UpdateWithoutWhereRegex` matched every UPDATE, including those with a WHERE clause.
  `DeleteWithoutWhereRegex` missed schema-qualified and quoted table names. Both replaced
  with token-aware checks.
- Confirmation is driven by assessed risk level rather than keyword membership; an unqualified
  UPDATE was rated high risk and then executed without asking.
- Keyword matching ignores comments and string literals, so `/*c*/DROP`, a leading semicolon,
  data-modifying CTEs and `DO` blocks are no longer missed, and a `DROP` inside a string
  literal no longer triggers a false alarm.
- Unclassified statements are treated as high-risk writes rather than as harmless.
- `execute_batch` uses a real transaction with one command per statement, replacing textual
  `BEGIN`/`COMMIT` concatenation.
- `restore` determines success from the exit code alone; it previously accepted any failure
  whose output mentioned "warning".
- Cancelling `backup` or `restore` kills the process tree.
- Subprocess arguments use `ArgumentList`, so paths with spaces work and values cannot inject
  flags. stdout is drained, removing a pipe-fill deadlock. TLS settings are carried through.
- `ArgumentException` when masking a command for a connection string with no password.
- Audit failures no longer discard a committed write; a relative `LogPath` resolves against
  the application directory.
- Cancellation propagates instead of being recorded as a failed query.
- `CriticalOperations` no longer duplicates when bound from configuration.
- Primary key detection is scoped by relation OID, fixing cross-schema constraint-name
  collisions.
- `get_table_ddl` preserves type modifiers, identity and generated columns, collations,
  foreign keys, checks and expression or partial indexes. `describe_table` returns the
  constraints it always advertised.
- Command timeouts, row caps and response-size caps.
- `SchemaTools` returns an error string instead of letting exceptions escape the tool call.
- A malformed connection string is reported and skipped instead of aborting startup.
- `Safety.EnableDryRun` is read; it was previously declared and never used.

### Changed

- `ModelContextProtocol` 0.7.0-preview.1 → 2.2.0
- `Npgsql` 10.0.1 → 10.0.3
- `Microsoft.Extensions.*` 10.0.2 → 10.0.11

### Notes

Quoting identifiers makes them case-sensitive: `create_table(tableName: "MyTable")` now
creates `"MyTable"` rather than `mytable`.
