# PostgreSQL MCP Server

A [Model Context Protocol](https://modelcontextprotocol.io) server that lets AI assistants
work with PostgreSQL — query, explore schemas, modify data, administer roles and take backups
— with safety guards that are enforced by the database rather than by guessing at SQL.

Works with Claude Desktop, Claude Code, Cursor, VS Code, Windsurf, Cline, Gemini CLI and Zed.

```
Your AI client  ──stdio──▶  PostgresMcpServer  ──▶  PostgreSQL
```

## Quick start

```bash
# 1. Build
dotnet publish PostgresMcpServer.csproj -c Release -o ./publish

# 2. Configure
cp appsettings.example.json publish/appsettings.json
#    edit the Databases section

# 3. Verify — before involving any AI client
./publish/PostgresMcpServer --check

# 4. Register with your client → docs/clients/
```

Or run [`scripts/setup.ps1`](scripts/setup.ps1) (Windows) / [`scripts/setup.sh`](scripts/setup.sh),
which does all four and prints the config block for your client.

## Documentation

**[Full documentation →](docs/README.md)**

| | |
|---|---|
| [Installation](docs/installation.md) | Prerequisites, build, publish, first-run check |
| [Configuration](docs/configuration.md) | Every setting, connection string cookbook, environment variables |
| [**Client setup**](docs/clients/README.md) | Claude Desktop · Claude Code · Cursor · VS Code · Windsurf · Cline · Gemini CLI · Zed · ChatGPT |
| [Tools](docs/tools.md) | All 17 tools and their arguments |
| [Safety model](docs/safety.md) | What the guards do, and what they do not guarantee |
| [Security](docs/security.md) | Database roles, least privilege, credentials |
| [Troubleshooting](docs/troubleshooting.md) | Error messages and what to do about them |
| [Development](docs/development.md) | Layout, tests, contributing |

## Setup in one block

Point your client at the executable. No arguments, no working directory.

```json
{
  "mcpServers": {
    "postgres": {
      "command": "C:\\path\\to\\Postgres-mcp\\publish\\PostgresMcpServer.exe"
    }
  }
}
```

VS Code uses `servers` and Zed uses `context_servers` — see
[client setup](docs/clients/README.md). Ready-to-copy files for every client are in
[`examples/`](examples/).

## Tools

| Group | Tools |
|-------|-------|
| Query | `query`, `list_databases`, `explain` |
| Schema | `list_schemas`, `list_tables`, `describe_table`, `get_table_ddl` |
| Execute | `execute`, `execute_batch` |
| Admin | `create_table`, `drop_table`, `list_roles`, `create_role`, `grant_privileges`, `get_database_stats` |
| Backup | `backup`, `restore` |

Details in [docs/tools.md](docs/tools.md).

## Safety

The guarantees rest on PostgreSQL, not on classifying SQL correctly:

- **Reads run in a `READ ONLY` transaction** that is always rolled back, so a write is refused
  by the database even if the statement was misclassified.
- **One call carries one statement.** Multi-statement input is refused, because it defeats
  every per-statement check.
- **Comments and literals are ignored** when matching keywords, so
  `INSERT INTO log VALUES ('user clicked DROP')` is not flagged while `/*c*/DROP TABLE users`
  is.
- **Unrecognised statements fail closed**, treated as high-risk writes.
- **Identifiers are quoted, never interpolated**; passwords are redacted before they reach the
  audit log.
- **`EXPLAIN ANALYZE` runs inside a rolled-back transaction**, so you can profile a `DELETE`
  without losing rows.

These reduce accidents. They are **not** access control — use a least-privilege database role.
See [safety](docs/safety.md) and [security](docs/security.md).

## Requirements

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) to build
- PostgreSQL (tested against 16 and 18)
- `pg_dump` / `pg_restore` on `PATH`, for the backup tools only
- Docker, for the integration tests only

## Tests

```bash
dotnet test "Postgres mcp.sln"
```

Unit tests cover statement classification, quoting, redaction and the shipped example files.
Integration tests start a throwaway PostgreSQL container and assert the runtime guarantees;
they are skipped, not failed, when Docker is unavailable.

## License

MIT — see [LICENSE](LICENSE).
