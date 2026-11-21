# PostgreSQL MCP Server

A [Model Context Protocol](https://modelcontextprotocol.io) server that lets AI assistants
work with PostgreSQL — query, explore schemas, modify data, administer roles and take backups
— with safety guards that are enforced by the database rather than by guessing at SQL.

Works with Claude Desktop, Claude Code, Cursor, VS Code, Windsurf, Cline, Gemini CLI and Zed.

```
Your AI client  ──stdio──▶  PostgresMcpServer  ──▶  PostgreSQL
```

## Quick start

**No .NET needed.** A release is one self-contained executable.

1. Download the archive for your platform from
   [Releases](https://github.com/EAX3010/Postgres-mcp/releases) and unpack it.

2. Run the setup.

   **Windows** — double-click `PostgresMcpServer.exe`, or in PowerShell:

   ```powershell
   .\PostgresMcpServer.exe --setup
   ```

   **macOS / Linux**

   ```bash
   ./PostgresMcpServer --setup
   ```

   It asks for your database — paste a `postgres://` URL, or answer one question at a time —
   tests the connection, saves it, and registers the server with the AI clients it finds on
   your computer. Nothing is saved until the connection has been tried.

3. Quit your AI client completely, reopen it, and ask it to *list the databases*.

If something does not work, `PostgresMcpServer --check` connects to every configured database
and prints the real error — see [troubleshooting](docs/troubleshooting.md).

<details>
<summary><b>Prefer to paste one block?</b> No setup, no config file</summary>

Give your client the path to the executable and a connection URL:

```json
{
  "mcpServers": {
    "postgres": {
      "command": "C:\\path\\to\\PostgresMcpServer\\PostgresMcpServer.exe",
      "env": {
        "DATABASE_URL": "postgres://user:password@localhost:5432/mydb"
      }
    }
  }
}
```

VS Code uses `servers` and Zed uses `context_servers` — see
[client setup](docs/clients/README.md). Ready-to-copy files for every client are in
[`examples/`](examples/).

</details>

<details>
<summary><b>Building from source instead</b> (contributors, or an unlisted platform)</summary>

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/EAX3010/Postgres-mcp.git
cd Postgres-mcp

dotnet publish PostgresMcpServer.csproj -c Release -o ./publish
./publish/PostgresMcpServer --setup
```

Add `-r win-x64 --self-contained` (or `linux-x64`, `osx-arm64`, …) to produce a single
executable that does not need the .NET runtime installed.

</details>

## Documentation

**[Full documentation →](docs/README.md)**

| | |
|---|---|
| [Installation](docs/installation.md) | Download a release, or build from source; guided setup |
| [Configuration](docs/configuration.md) | Every setting, connection URLs and strings, environment variables |
| [**Client setup**](docs/clients/README.md) | Claude Desktop · Claude Code · Cursor · VS Code · Windsurf · Cline · Gemini CLI · Zed · ChatGPT |
| [Tools](docs/tools.md) | All 17 tools and their arguments |
| [Safety model](docs/safety.md) | What the guards do, and what they do not guarantee |
| [Security](docs/security.md) | Database roles, least privilege, credentials |
| [Troubleshooting](docs/troubleshooting.md) | Error messages and what to do about them |
| [Development](docs/development.md) | Layout, tests, contributing |

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

- **PostgreSQL** - tested against 16 and 18. That is the only hard requirement.
- Nothing else to run a release: the .NET runtime is bundled in the executable.
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) - only to build from source.
- `pg_dump` / `pg_restore` on `PATH` - only for the `backup` and `restore` tools.
- Docker - only to run the integration tests.

## Tests

```bash
dotnet test "Postgres mcp.sln"
```

Unit tests cover statement classification, quoting, redaction and the shipped example files.
Integration tests start a throwaway PostgreSQL container and assert the runtime guarantees;
they are skipped, not failed, when Docker is unavailable.

## License

MIT — see [LICENSE](LICENSE).
