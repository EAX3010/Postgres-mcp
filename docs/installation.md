# Installation

## Install from a release

The quickest route. Releases ship **self-contained** archives — the .NET runtime is bundled,
so nothing else needs installing.

1. Download the archive for your platform from the
   [Releases page](https://github.com/EAX3010/Postgres-mcp/releases) and unpack it.
2. Open a terminal in that folder and create a configuration file:

   ```powershell
   .\PostgresMcpServer.exe --init     # Windows PowerShell
   ```

   ```bash
   ./PostgresMcpServer --init          # macOS / Linux
   ```

3. Edit the `appsettings.json` it created, replacing `CHANGE_ME` with your password.
4. Confirm it connects, using the same form as above with `--check`.
5. Register the executable with your client — see [clients/](clients/README.md).

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
| `PostgresMcpServer` / `.exe` | The server. Self-contained |
| `START-HERE.txt` | The five steps above |
| `appsettings.example.json` | Every setting, documented, with seven example connections |
| `examples/` | Ready-to-copy client configs for Claude Desktop, Claude Code, Cursor, VS Code, Windsurf, Cline, Gemini CLI and Zed |
| `docs/` | This documentation |
| `README.md`, `CHANGELOG.md`, `LICENSE` | |

No `appsettings.json` ships in a release — `--init` writes it, so nobody inherits someone
else's credentials.

Verify a download against the `SHA256SUMS.txt` published with the release:

```bash
sha256sum -c SHA256SUMS.txt --ignore-missing
```

Archives are built for `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64`.

---

## Build from source

### Prerequisites

| | Needed for |
|---|---|
| [.NET 10.0 SDK](https://dotnet.microsoft.com/download) | Building |
| PostgreSQL server | Everything. Tested against PostgreSQL 16 and 18 |
| `pg_dump` / `pg_restore` on `PATH` | The `backup` and `restore` tools only |
| Docker | The integration test suite only |

The published output is **framework-dependent**: whoever runs it needs the .NET 10 runtime.
See [Self-contained builds](#self-contained-builds) to remove that requirement.

### Build

```bash
git clone https://github.com/EAX3010/Postgres-mcp.git
cd Postgres-mcp

dotnet publish PostgresMcpServer.csproj -c Release -o ./publish
```

> The repository root contains both a `.csproj` and a `.sln`, so bare `dotnet build` cannot
> choose between them and fails with `MSB1011`. Always name one:
> `dotnet build PostgresMcpServer.csproj` or `dotnet build "Postgres mcp.sln"`.

There is a setup script that does the build, copies the config template and runs the check:

```powershell
.\scripts\setup.ps1      # Windows
```

```bash
./scripts/setup.sh       # macOS / Linux
```

### Configure

Let the server write its own starter configuration:

```bash
./publish/PostgresMcpServer --init
```

Then edit the `appsettings.json` it created, replacing `CHANGE_ME` with your password. Every
available setting is documented in `appsettings.example.json` beside it.

The file is optional — the whole configuration can come from `POSTGRESMCP_` environment
variables instead. The server needs at least one database from one source. See
[configuration.md](configuration.md).

## Check it before involving an AI client

```bash
./publish/PostgresMcpServer --check
```

This validates the configuration and opens a real connection to every database:

```
PostgresMcpServer configuration check
------------------------------------------------------------
Application directory : C:\...\publish\
Config file           : C:\...\publish\appsettings.json
Audit log             : C:\...\publish\audit.log
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

## Register with a client

See [clients/](clients/README.md). In every case you supply one absolute path to the
executable — no arguments, and no working-directory setting.

## Other commands

```bash
PostgresMcpServer --help      # usage
PostgresMcpServer --version   # version
PostgresMcpServer --check     # validate config and connect
PostgresMcpServer             # run as an MCP server over stdio
```

Started with no arguments it waits for JSON-RPC on stdin; that is how clients launch it, and
it is not useful to run by hand.

## Self-contained builds

To remove the .NET runtime requirement for end users:

```bash
dotnet publish PostgresMcpServer.csproj -c Release -r win-x64 --self-contained -o ./publish
```

Use `osx-arm64`, `osx-x64` or `linux-x64` as appropriate. The output is larger but runs
anywhere.

Ahead-of-time compilation (`PublishAot`) would give a single fast-starting binary, but the
MCP SDK discovers tools by reflection, so it needs JSON source-generation work first. That is
not done here.

## Upgrading

```bash
git pull
dotnet publish PostgresMcpServer.csproj -c Release -o ./publish
```

`publish` does not delete files it no longer produces, and your `appsettings.json` is left
alone. Restart your MCP client afterwards. Run `--check` to confirm the new build still reads
your configuration.
