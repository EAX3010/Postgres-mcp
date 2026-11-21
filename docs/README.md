# Documentation

PostgreSQL MCP server — full documentation. Start at [installation](installation.md), then
pick your client from [clients](clients/README.md).

## Map

| Page | What it covers |
|------|----------------|
| [installation.md](installation.md) | Download a release, the guided setup, building from source |
| [configuration.md](configuration.md) | Every setting, connection URLs and strings, environment variables |
| [clients/](clients/README.md) | Setup for Claude Desktop, Claude Code, Cursor, VS Code, Windsurf, Cline, Gemini CLI, Zed — and why ChatGPT is not supported |
| [tools.md](tools.md) | All 17 tools, their arguments and what they return |
| [safety.md](safety.md) | How the guards work and what they do and do not guarantee |
| [security.md](security.md) | Database roles, least privilege, credential handling |
| [troubleshooting.md](troubleshooting.md) | Error messages and what to do about them |
| [development.md](development.md) | Repository layout, tests, contributing |

## The short version

```bash
# 1. Download a release for your platform and unpack it.
#    One self-contained executable: no .NET runtime required.

# 2. Run the guided setup. It asks for your connection (a postgres:// URL will do),
#    tests it, saves it, and registers the server with the AI clients it finds.
./PostgresMcpServer --setup

# 3. Quit your AI client completely and reopen it.

# If anything misbehaves, this connects to every database and prints the real error:
./PostgresMcpServer --check
```

Windows PowerShell uses `.\PostgresMcpServer.exe` in place of `./PostgresMcpServer` -
see [running these commands](installation.md#running-these-commands).

Building from source instead? See [installation.md](installation.md#build-from-source).

## How the pieces fit

```
   Your AI client (Claude Desktop, Cursor, VS Code, …)
        │
        │  stdio — JSON-RPC on stdin/stdout
        ▼
   PostgresMcpServer.exe
        │
        ├── SafetyGuard ....... classifies the statement, assigns a risk level,
        │                       decides whether confirmation is required
        ├── PostgresService ... executes it; reads run in a rolled-back
        │                       READ ONLY transaction
        ├── AuditLogger ....... appends a JSON line per operation, with
        │                       passwords redacted
        │
        ▼
   PostgreSQL
```

The safety guarantees rest on the database, not on parsing SQL correctly — see
[safety.md](safety.md).

## Design notes

Three decisions explain most of the code:

- **Read-only means a read-only transaction**, not a keyword check. `query` issues
  `SET TRANSACTION READ ONLY` and rolls back, so PostgreSQL refuses writes regardless of how
  the statement was classified.
- **One call carries one statement.** Multi-statement input is refused, because it defeats
  every per-statement check.
- **Unrecognised statements are treated as dangerous**, not as harmless.
