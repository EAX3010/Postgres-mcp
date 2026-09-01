# Configuration

Settings come from two sources, and **neither is individually required**. The server needs at
least one database from one of them:

1. **`appsettings.json` beside the executable** — optional. `publish` and `build` copy it to
   the output directory, and the server resolves it against its own location rather than the
   working directory, so no `cwd` setting is needed in any MCP client config.
2. **`POSTGRESMCP_`-prefixed environment variables** — optional, and they override the file
   where both set the same key.

[`appsettings.example.json`](../appsettings.example.json) is a working example of every
setting. Copy it and edit.

Run `PostgresMcpServer --check` after any change; it prints the effective settings and
connects to every database.

---

## Databases

A map of connection name → Npgsql connection string. **The name is what you say to the
assistant** ("query the `analytics` database") and what every tool takes as its `database`
argument.

```json
"Databases": {
  "local":        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=YOUR_PASSWORD",
  "app_readonly": "Host=localhost;Port=5432;Database=myapp;Username=mcp_readonly;Password=YOUR_PASSWORD",
  "app_write":    "Host=localhost;Port=5432;Database=myapp;Username=mcp_writer;Password=YOUR_PASSWORD;Include Error Detail=true"
}
```

Two entries may point at the same database through different roles. That is a useful pattern:
give the assistant `app_readonly` by default and reach for `app_write` deliberately.

A connection string that fails to parse is skipped with a `[CONFIG-ERROR]` on stderr. The
remaining databases still load, and `list_databases` reports which were dropped.

### Connection string cookbook

Every line below appears in `appsettings.example.json` and is parsed by the test suite.

| Goal | Add to the connection string |
|------|------------------------------|
| Non-default port | `Port=6432` |
| Default schema | `Search Path=analytics,public` |
| Fuller constraint-violation messages | `Include Error Detail=true` |
| Encrypted connection | `SSL Mode=Require` |
| Verified certificate | `SSL Mode=VerifyFull;Root Certificate=/path/to/server-ca.pem` |
| Connect timeout, seconds | `Timeout=15` |
| Per-command timeout, overriding `Limits` | `Command Timeout=60` |
| Long-lived link through a firewall | `Keepalive=30` |
| Pool sizing, e.g. behind PgBouncer | `Maximum Pool Size=10;Minimum Pool Size=1;Connection Idle Lifetime=60` |

On Windows, backslashes inside a JSON string must be doubled:
`Root Certificate=C:\\certs\\server-ca.pem`.

---

## Safety

```json
"Safety": {
  "RequireConfirmation": true,
  "EnableDryRun": true,
  "ConfirmAtRiskLevel": "High",
  "AllowMultiStatement": false,
  "CriticalOperations": [ "DROP", "TRUNCATE", "DELETE", "ALTER", "GRANT", "REVOKE" ]
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `RequireConfirmation` | `true` | Require `confirm=true` for risky operations. `false` disables confirmation entirely |
| `EnableDryRun` | `true` | Allow `dryRun=true` previews. `false` makes the write tools refuse a preview request |
| `ConfirmAtRiskLevel` | `High` | Lowest risk level that forces confirmation: `Low`, `Medium`, `High`, `Critical` |
| `AllowMultiStatement` | `false` | Permit several statements per call. Leave off; it defeats every per-statement check |
| `CriticalOperations` | the six above | Keywords that always require confirmation. Your list **replaces** the defaults rather than adding to them |

**Worth changing:** `ConfirmAtRiskLevel` defaults to `High`, so `INSERT` and
`UPDATE ... WHERE` run without asking. Set it to `"Medium"` to gate those too.

See [safety.md](safety.md) for how risk levels are assigned.

---

## Limits

```json
"Limits": {
  "CommandTimeoutSeconds": 30,
  "MaxRows": 1000,
  "MaxResponseBytes": 1000000
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `CommandTimeoutSeconds` | `30` | Per-command timeout. `0` disables it. A `Command Timeout` in a connection string wins for that database |
| `MaxRows` | `1000` | Hard ceiling on rows from `query`, whatever `limit` the caller passes |
| `MaxResponseBytes` | `1000000` | Approximate ceiling on a serialized response. Rows are dropped and `truncated: true` set, rather than returning an enormous payload |

---

## Audit

```json
"Audit": {
  "Enabled": true,
  "LogPath": "audit.log",
  "LogToConsole": false
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Enable audit logging |
| `LogPath` | `audit.log` | Relative paths resolve against the application directory, not the working directory. Absolute paths are used as given |
| `LogToConsole` | `false` | Also write each entry to stderr |

One JSON object per line, covering executed statements, dry runs and operations refused for
want of confirmation:

```json
{"Timestamp":"2025-01-15T10:30:00Z","InstanceId":"a1b2c3d4","Database":"production","Operation":"DELETE","Query":"DELETE FROM users WHERE id = 1","User":"jo","DryRun":false,"Confirmed":true,"Rejected":false,"RiskLevel":"high","Success":true,"RowsAffected":1}
```

`PASSWORD '...'` literals become `PASSWORD '[REDACTED]'` before anything is written, so role
credentials never reach the log. A failure to write is reported once on stderr and never
discards a database result that already applied.

### Multiple instances

Each instance gets an 8-character `InstanceId` and appends with a share mode that tolerates
concurrent readers and writers, retrying briefly on contention. Ordering between instances is
not guaranteed; use `InstanceId` to separate the streams.

---

## Environment variables

Any setting can come from the environment with a `POSTGRESMCP_` prefix, using `__` (double
underscore) as the section separator:

```bash
POSTGRESMCP_Databases__production="Host=db.internal;Database=app;Username=mcp;Password=..."
POSTGRESMCP_Safety__ConfirmAtRiskLevel="Medium"
POSTGRESMCP_Limits__MaxRows="500"
POSTGRESMCP_Audit__LogPath="/var/log/postgres-mcp/audit.log"
```

This is the cleanest way to keep credentials out of a file. Most MCP clients let you set them
per-server — see [`examples/env-only.json`](../examples/env-only.json).

| Config path | Environment variable |
|-------------|---------------------|
| `Databases:local` | `POSTGRESMCP_Databases__local` |
| `Safety:ConfirmAtRiskLevel` | `POSTGRESMCP_Safety__ConfirmAtRiskLevel` |
| `Limits:MaxRows` | `POSTGRESMCP_Limits__MaxRows` |
| `Audit:Enabled` | `POSTGRESMCP_Audit__Enabled` |

Environment values win over the file. A list such as `CriticalOperations` is awkward to set
this way (`POSTGRESMCP_Safety__CriticalOperations__0`) — prefer the file for those.

---

## Reference: full file

```json
{
  "Databases": {
    "local": "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=YOUR_PASSWORD"
  },
  "Safety": {
    "RequireConfirmation": true,
    "EnableDryRun": true,
    "ConfirmAtRiskLevel": "High",
    "AllowMultiStatement": false,
    "CriticalOperations": [ "DROP", "TRUNCATE", "DELETE", "ALTER", "GRANT", "REVOKE" ]
  },
  "Limits": {
    "CommandTimeoutSeconds": 30,
    "MaxRows": 1000,
    "MaxResponseBytes": 1000000
  },
  "Audit": {
    "Enabled": true,
    "LogPath": "audit.log",
    "LogToConsole": false
  }
}
```
