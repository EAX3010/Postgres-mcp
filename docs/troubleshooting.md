# Troubleshooting

**Start here.** This answers most questions in one step, and it does not need an MCP client:

```bash
./PostgresMcpServer --check
```

On Windows PowerShell: `.\PostgresMcpServer.exe --check`. Every command below is written in
the macOS and Linux form - see
[running these commands](installation.md#running-these-commands).

It validates the configuration, prints the effective settings, and opens a real connection to
every database. If it passes and your client still shows nothing, the problem is the client
registration, not the server — run `--setup` again and let it register the client.

---

## Starting the program

### Windows shows "Windows protected your PC"

The release executables are not code-signed, so SmartScreen warns about a download it has not
seen before. Choose **More info**, then **Run anyway**. Verify the download against
`SHA256SUMS.txt` first if you want assurance it is the published file.

### macOS says the program cannot be opened or verified

For the same reason, macOS quarantines a file downloaded through a browser. Remove the flag
once, from the folder you unpacked:

```bash
xattr -d com.apple.quarantine PostgresMcpServer
```

### `Permission denied` on macOS or Linux

The archive tool dropped the executable bit: `chmod +x PostgresMcpServer`.

### `You must install .NET to run this application`

Only a build from source says this: its default output is framework-dependent. Install the
.NET 10 runtime, or produce a self-contained build — see
[installation.md](installation.md#self-contained-builds). Release downloads bundle the runtime.

### `MSB1011: Specify which project or solution file to use`

The repository root has both a `.csproj` and a `.sln`. Name one:
`dotnet build PostgresMcpServer.csproj` or `dotnet test "Postgres mcp.sln"`.

---

## Configuration

### `[FATAL] No databases are configured`

No source produced a connection. Run the guided setup:

```bash
./PostgresMcpServer --setup
```

Alternatively set `DATABASE_URL` or `POSTGRESMCP_Databases__<name>` in the environment and use
no file at all. See [configuration.md](configuration.md).

`--check` prints the exact path it looked for.

### `[CONFIG-ERROR] Database 'x' was skipped`

That connection failed to parse. The other databases still load, and `list_databases` reports
which were dropped. `--check` shows the parse error.

### `The connection URL parameter '...' is not one this server understands`

The URL carries a parameter meant for another tool — Prisma's `pgbouncer=true` and
`connection_limit` are the usual ones. Remove it. The parameters that are understood are
listed in [configuration.md](configuration.md#connection-urls).

### `The connection URL has a port that is not a number` / `has an '@' after a '?'`

Almost always a password containing `?`, which ends the address early. Write it as `%3F`.
The message does not show the URL on purpose, so the password stays out of logs.

### `DATABASE_URL is set but ignored`

`DATABASE_URL` is only used when no database is configured any other way — see
[why](configuration.md#database_url). Remove the entries from `appsettings.json`, or add the
URL to it under a name of its own.

---

## Connecting

### `password authentication failed for user "..."`

The password is wrong, or still the `CHANGE_ME` placeholder that `--init` writes. Run
`--setup` to enter it again; it tests the connection before saving.

### `No connection could be made because the target machine actively refused it`

Nothing is listening. Check the server is running and on the expected port:

```bash
pg_isready -h localhost -p 5432
```

### `no pg_hba.conf entry for host ...`

PostgreSQL is running but rejects connections from your address. Add a matching `pg_hba.conf`
line and reload the server.

### `database "..." does not exist`

The `Database=` part of the connection string names a database that is not there. Connect to
`postgres` and run `\l` to list them.

### `Cannot load library libgssapi_krb5.so.2` on Linux

Harmless. The database driver looks for Kerberos support before connecting, and a minimal
Linux install does not have the library. The connection carries on without it, and the notice
goes to stderr, so it does not disturb an AI client. To silence it, install the library:
`libgssapi-krb5-2` on Debian and Ubuntu, `krb5-libs` on Fedora and RHEL.

### Connections hang

Add `Timeout=15` to fail fast instead of waiting, and check for a firewall between you and
the server.

---

## Client shows no tools

### It shows nothing at all

1. Run `--check`. If that fails, fix the server first.
2. **Restart the client fully.** All of them read config at startup. On Windows, Claude
   Desktop keeps running in the tray — quit from the tray icon, not the window.
3. **Let `--setup` write the registration.** It puts the right path, correctly escaped, under
   the right key in the right file. If you moved the folder since registering, it re-points
   the client.

If you registered by hand, the usual mistakes are:

- **Single backslashes.** On Windows a JSON path needs `\\`. A single-backslash path fails
  silently.
- **A relative path** to the executable. Use an absolute one.
- **The wrong file or key.** VS Code uses `servers`, Zed uses `context_servers`, everything
  else uses `mcpServers`. See [clients/](clients/README.md).

### It reports a protocol error

stdout is the JSON-RPC channel; anything else written there corrupts it. All logging in this
server goes to stderr. If you have added a logging provider, keep it off stdout.

To see for yourself that stdout is clean:

```bash
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"probe","version":"1.0"}}}' \
  | ./PostgresMcpServer 2>/dev/null
```

Only JSON should appear.

### Where the client keeps its logs

- **Claude Desktop** — `%APPDATA%\Claude\logs\mcp-server-postgres.log`
- **Claude Code** — `claude mcp list`, and `/mcp` in a session
- **VS Code** — Output panel, MCP channel
- **Cursor** — Settings, MCP section

---

## Tool errors

### `Multiple SQL statements in a single call are not allowed`

By design — it defeats every per-statement check. Send one statement, or use `execute_batch`
with one statement per array element. See [safety.md](safety.md).

### `This tool only runs read-only statements`

`query` refuses writes. Use `execute`. If you believe the statement is read-only, note that
`SELECT ... FOR UPDATE` takes locks and a data-modifying CTE is a write.

### `cannot execute INSERT in a read-only transaction`

PostgreSQL refusing a write inside `query`'s transaction — the protection working as
intended. Use `execute`.

### `requiresConfirmation`

Not an error. The statement was assessed as risky; re-send with `confirm=true`, or preview it
with `dryRun=true` first. Lower `Safety.ConfirmAtRiskLevel` to be asked more often, raise it
to be asked less.

### Results are cut off

`truncated: true` means the row cap (`Limits.MaxRows`) or the response-size cap
(`Limits.MaxResponseBytes`) stopped it. Narrow the query, or raise the limits in
[configuration.md](configuration.md#limits).

### `Timeout during reading attempt`

The query exceeded `Limits.CommandTimeoutSeconds` (30 by default). Optimise it — `explain`
helps — or raise the timeout.

### `permission denied for table ...`

The configured role lacks the privilege. That is usually correct; see
[security.md](security.md). Grant it deliberately, or use a connection entry with more
rights.

---

## Backup and restore

### `pg_dump was not found`

The PostgreSQL client tools are not on `PATH`. Add the `bin` directory — on Windows typically
`C:\Program Files\PostgreSQL\18\bin` — and restart your client so it inherits the change.

### `server version mismatch`

`pg_dump` must be at least as new as the server. Install client tools matching your server's
major version.

### A restore reported failure

Success is determined by exit code alone; the `error` field carries what `pg_restore`
printed. A partially restored database is possible — check before retrying.

---

## Audit log

### `[AUDIT-FAILURE] Cannot write ...`

The log path is not writable. Reported once per session, and it never discards a database
result. Fix the path or permissions; note the server continues **unaudited** until you do.

### The log is not where you expected

A relative `LogPath` resolves against the application directory, not the working directory.
`--check` prints the resolved path.

---

## Still stuck

Run `--check` and include its output when reporting a problem, along with your client, its
version, and the config block you used with credentials removed.
