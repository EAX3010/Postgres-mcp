# Installation

## Install from a release

The quickest route. Each release is one **self-contained** executable per platform — the .NET
runtime is bundled, so nothing else needs installing.

1. Download the archive for your platform from the
   [Releases page](https://github.com/EAX3010/Postgres-mcp/releases) and unpack it.
2. Run the guided setup:

   ```powershell
   .\PostgresMcpServer.exe --setup     # Windows PowerShell - or just double-click the exe
   ```

   ```bash
   ./PostgresMcpServer --setup          # macOS / Linux
   ```

3. Quit your AI client completely and reopen it, then ask it to *list the databases*.

### What the setup does

1. **Asks for a connection.** Paste a `postgres://` URL, or press Enter and answer host, port,
   database, username and password one at a time. A typed password is not shown.
2. **Tests it** by really connecting. If that fails it shows the server's error and lets you
   retry, save it anyway, or quit. Nothing has been written at this point.
3. **Saves it** to `appsettings.json` beside the executable, under a name you choose — the
   name is what you call the database when talking to the assistant. An existing file keeps
   all its other settings, and the previous version stays beside it as `appsettings.json.bak`.
4. **Registers the server** with the AI clients it finds on the computer:

   | Client | What happens |
   |--------|--------------|
   | Claude Desktop, Cursor, Windsurf, Cline, Gemini CLI, VS Code, Zed | The server is added to the client's config file. Everything else in the file is left as it was, and the previous version is kept beside it with `.bak` added |
   | Claude Code | The `claude mcp add` command is printed for you to run |

   A config file that contains comments — common in Zed and VS Code — is **not** rewritten,
   because that would delete them. The block to paste is printed instead, with the real path
   already in it. If a *different* server called `postgres` is already registered, you are
   asked before it is replaced.

Run the setup again whenever you like: to add a second database, or to register with a client
installed later. When the existing connections work, pressing Enter skips straight to the
clients.

Starting the executable from a terminal with no arguments, or double-clicking it, does the
same as `--setup`. An AI client starts it through pipes rather than a terminal, which is how
the two are told apart; `--stdio` forces server mode from a terminal.

### Running these commands

Every `PostgresMcpServer ...` command in this documentation is written in the macOS and Linux
form. Translate it for your shell:

| Shell | Form | Example |
|-------|------|---------|
| Windows PowerShell | `.\PostgresMcpServer.exe` | `.\PostgresMcpServer.exe --check` |
| Windows Command Prompt | `PostgresMcpServer.exe` | `PostgresMcpServer.exe --check` |
| macOS / Linux / Git Bash | `./PostgresMcpServer` | `./PostgresMcpServer --check` |

The leading `.\` or `./` matters: it says "the program in this folder". Without it, the shell
searches `PATH` and reports that the command does not exist.

To run it from somewhere else, give the full path. PowerShell needs the call operator `&` when
the path is quoted:

```powershell
& "C:\Tools\PostgresMcpServer\PostgresMcpServer.exe" --check
```

```bash
/opt/postgres-mcp/PostgresMcpServer --check
```

On macOS and Linux, mark the binary executable once after unpacking if your archive tool did
not preserve the bit:

```bash
chmod +x PostgresMcpServer
```

### What is in a release archive

| | |
|---|---|
| `PostgresMcpServer` / `.exe` | The server: one file, self-contained |
| `START-HERE.txt` | The one command above |
| `appsettings.example.json` | Every setting, documented, with example connections |
| `examples/` | Ready-to-copy client configs for Claude Desktop, Claude Code, Cursor, VS Code, Windsurf, Cline, Gemini CLI and Zed |
| `docs/` | This documentation |
| `README.md`, `CHANGELOG.md`, `LICENSE` | |

No `appsettings.json` ships in a release — the setup writes it, so nobody inherits someone
else's credentials.

Verify a download against the `SHA256SUMS.txt` published with the release:

```bash
sha256sum -c SHA256SUMS.txt --ignore-missing
```

Archives are built for `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64`.

---

## Setting it up by hand

Everything the setup does can be done manually, which suits scripted installs.

1. **Supply a connection**, one of:
   - `./PostgresMcpServer --init` writes a starter `appsettings.json` beside the executable;
     edit it and replace `CHANGE_ME`. Every setting is documented in
     `appsettings.example.json`.
   - Set `DATABASE_URL`, or `POSTGRESMCP_Databases__<name>`, in the environment and use no
     file at all. See [configuration.md](configuration.md#environment-variables).
2. **Check it** — see below.
3. **Register the executable** with your client — see [clients/](clients/README.md). In every
   case you supply one absolute path to the executable: no arguments, and no
   working-directory setting.

## Check it before involving an AI client

```bash
./PostgresMcpServer --check
```

This validates the configuration and opens a real connection to every database:

```
PostgresMcpServer configuration check
------------------------------------------------------------
Application directory : C:\Tools\PostgresMcpServer\
Config file           : C:\Tools\PostgresMcpServer\appsettings.json
Audit log             : C:\Tools\PostgresMcpServer\audit.log
Confirm at risk level : High
Multi-statement SQL   : refused
Max rows / response   : 1000 rows / 1,000,000 bytes

Databases (1):

  [ OK ] local
         localhost:5432/myapp as mcp_readonly
         Connected as mcp_readonly to myapp - PostgreSQL 18.4

All databases reachable. The server is ready to register with an MCP client.
```

It exits `0` when everything connects and `1` otherwise, so it works in a health check or CI
step. Diagnosing setup here is much easier than through a client that reports every problem
as "server failed to start".

## Commands

```bash
PostgresMcpServer --setup     # guided setup: connect a database, register with clients
PostgresMcpServer --check     # validate config and connect
PostgresMcpServer --init      # write a starter appsettings.json to edit by hand
PostgresMcpServer --help      # usage
PostgresMcpServer --version   # version
PostgresMcpServer --stdio     # run as an MCP server even when started from a terminal
PostgresMcpServer             # from an AI client: the MCP server. From a terminal: --setup
```

---

## Build from source

### Prerequisites

| | Needed for |
|---|---|
| [.NET 10.0 SDK](https://dotnet.microsoft.com/download) | Building |
| PostgreSQL server | Everything. Tested against PostgreSQL 16 and 18 |
| `pg_dump` / `pg_restore` on `PATH` | The `backup` and `restore` tools only |
| Docker | The integration test suite only |

### Build

```bash
git clone https://github.com/EAX3010/Postgres-mcp.git
cd Postgres-mcp

dotnet publish PostgresMcpServer.csproj -c Release -o ./publish
./publish/PostgresMcpServer --setup
```

> The repository root contains both a `.csproj` and a `.sln`, so bare `dotnet build` cannot
> choose between them and fails with `MSB1011`. Always name one:
> `dotnet build PostgresMcpServer.csproj` or `dotnet build "Postgres mcp.sln"`.

That output is **framework-dependent**: whoever runs it needs the .NET 10 runtime.

### Self-contained builds

To remove the .NET runtime requirement, name a platform:

```bash
dotnet publish PostgresMcpServer.csproj -c Release -r win-x64 --self-contained -o ./publish
```

Use `osx-arm64`, `osx-x64`, `linux-x64` or `linux-arm64` as appropriate. The project file
turns a self-contained publish into a single executable, which is what the release workflow
ships.

Ahead-of-time compilation (`PublishAot`) would give a smaller, faster-starting binary, but the
MCP SDK discovers tools by reflection, so it needs JSON source-generation work first. That is
not done here.

---

## Upgrading

**From a release:** quit your AI client, then unpack the new archive over the old folder. The
archive contains no `appsettings.json`, so yours is left alone. If you unpack somewhere else
instead, copy `appsettings.json` across and run `--setup` once — it re-points each client at
the new location.

**From source:**

```bash
git pull
dotnet publish PostgresMcpServer.csproj -c Release -o ./publish
```

Either way, run `--check` to confirm the new build still reads your configuration.
