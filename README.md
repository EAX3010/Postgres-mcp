# PostgreSQL MCP Server

A [Model Context Protocol](https://modelcontextprotocol.io) server that lets AI assistants
work with PostgreSQL — query, explore schemas, modify data, administer roles and take backups
— with safety guards that are enforced by the database rather than by guessing at SQL.

Works with Claude Desktop, Claude Code, Cursor, VS Code, Windsurf, Cline, Gemini CLI and Zed.

```
Your AI client  ──stdio──▶  PostgresMcpServer  ──▶  PostgreSQL
```

## Quick start

**No .NET needed.** Releases ship self-contained binaries with the runtime bundled.

1. Download the archive for your platform from
   [Releases](https://github.com/yourusername/postgres-mcp-server/releases) and unpack it.

2. Open a terminal **in the folder you unpacked** and run:

   **Windows (PowerShell)**

   ```powershell
   .\PostgresMcpServer.exe --init     # writes appsettings.json next to the exe
   # now edit appsettings.json and replace CHANGE_ME with your password
   .\PostgresMcpServer.exe --check    # connects, and tells you what it found
   ```

   **macOS / Linux**

   ```bash
   ./PostgresMcpServer --init
   # now edit appsettings.json and replace CHANGE_ME with your password
   ./PostgresMcpServer --check
   ```

   `--check` prints `[ OK ]` per database when it works. If it does not, the error it
   prints is the real one — see [troubleshooting](docs/troubleshooting.md).

3. Register the executable with your AI client —
   **[pick your client here](docs/clients/README.md)**. Ready-to-copy configs for each are in
   the `examples/` folder inside the archive.

<details>
<summary><b>Building from source instead</b> (contributors, or an unlisted platform)</summary>

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/yourusername/postgres-mcp-server.git
cd postgres-mcp-server

dotnet publish PostgresMcpServer.csproj -c Release -o ./publish
./publish/PostgresMcpServer --init
./publish/PostgresMcpServer --check
```

Or run [`scripts/setup.ps1`](scripts/setup.ps1) / [`scripts/setup.sh`](scripts/setup.sh),
which does all of that and prints the config block for your client with the real path
already filled in.

Add `-r win-x64 --self-contained` (or `linux-x64`, `osx-arm64`, …) to produce a build that
does not need the .NET runtime installed.

</details>

## Documentation

**[Full documentation →](docs/README.md)**

| | |
|---|---|
| [Installation](docs/installation.md) | Download a release, or build from source; first-run check |
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
      "command": "C:\\path\\to\\PostgresMcpServer\\PostgresMcpServer.exe"
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

- **PostgreSQL** - tested against 16 and 18. That is the only hard requirement.
- Nothing else to run a release: the .NET runtime is bundled in the archive.
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
