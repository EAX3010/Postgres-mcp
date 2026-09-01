# Security

The in-process guards described in [safety.md](safety.md) reduce accidents. **Database
permissions are the actual boundary.** Configure the role so that what you do not want to
happen is impossible, not merely discouraged.

## Do not connect as `postgres`

A superuser can do anything, and every guard in this server is advisory by comparison. Create
a purpose-built role.

### Read-only role

The right default for exploration and analysis.

```sql
CREATE ROLE mcp_readonly WITH LOGIN PASSWORD 'pick_a_strong_one';
GRANT CONNECT ON DATABASE mydb TO mcp_readonly;
GRANT USAGE ON SCHEMA public TO mcp_readonly;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO mcp_readonly;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO mcp_readonly;
```

With this role, `execute` and `drop_table` fail at the server whatever any tool or prompt
decides.

### Writer role, without DDL

Can change data but not schema.

```sql
CREATE ROLE mcp_writer WITH LOGIN PASSWORD 'pick_a_strong_one';
GRANT CONNECT ON DATABASE mydb TO mcp_writer;
GRANT USAGE ON SCHEMA public TO mcp_writer;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO mcp_writer;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA public TO mcp_writer;
ALTER DEFAULT PRIVILEGES IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mcp_writer;
```

### Restricting to specific tables

```sql
GRANT SELECT ON public.orders, public.customers TO mcp_readonly;
```

Grant per table instead of `ALL TABLES`, and skip the `ALTER DEFAULT PRIVILEGES` line so new
tables are not exposed automatically.

## Two entries, one database

Configure both roles and let the assistant use the read-only one by default:

```json
"Databases": {
  "app":       "Host=localhost;Database=myapp;Username=mcp_readonly;Password=...",
  "app_write": "Host=localhost;Database=myapp;Username=mcp_writer;Password=..."
}
```

Reaching for `app_write` then becomes a deliberate act, and the audit log records which
connection was used.

## Credentials

- **Never commit `appsettings.json`.** `.gitignore` excludes it. `appsettings.example.json`
  is the template and contains only placeholders — a test asserts no real-looking password
  ships in it.
- **Prefer environment variables** where a file is awkward. See
  [configuration.md](configuration.md#environment-variables) and
  [`examples/env-only.json`](../examples/env-only.json).
- **Passwords never reach the audit log.** `PASSWORD '...'` literals are redacted before
  writing, including for statements you send through `execute` yourself.
- **`appsettings.json` is readable by anyone who can read the directory.** It holds plaintext
  credentials; set file permissions accordingly, or use the environment.

## Auditing

Every executed statement, dry run and refused operation is recorded as one JSON line. Review
it — the refused entries are the interesting ones.

```bash
# operations that were refused for want of confirmation
grep '"Rejected":true' audit.log

# everything critical
grep '"RiskLevel":"critical"' audit.log
```

Set `Audit.LogPath` to a directory the server can write but the assistant cannot conveniently
rewrite. Auditing failures degrade gracefully — they are reported once on stderr and never
discard a result — so a full disk will not stop the server, and will not stop it working
unaudited either. Monitor for `[AUDIT-FAILURE]`.

## Network exposure

This server speaks stdio. The security boundary is "whoever can run this process", normally
just you.

**Do not put it on a network.** Exposing these tools over HTTP turns the boundary into
"whoever can reach this URL", and they include `drop_table`, `create_role` and
`grant_privileges`. If you need remote access, build a separate, deliberately narrow service
exposing only `query` against a read-only role. See [clients/chatgpt.md](clients/chatgpt.md).

## Client-side risks

- **Auto-approval defeats confirmation.** Clients that can auto-approve tool calls (Cline,
  among others) will click through every `confirm` prompt. Leave those lists empty.
- **Prompt injection reaches your database.** If the assistant reads untrusted content — an
  issue tracker, a web page, a table of user-submitted text — that content can attempt to
  steer tool calls. A read-only role is the reliable mitigation; the confirmation prompts are
  not, because the model composes the arguments.

## Checklist

- [ ] Connecting as a purpose-built role, not `postgres`
- [ ] Read-only role for the default connection
- [ ] `appsettings.json` not committed, permissions restricted
- [ ] `RequireConfirmation` on, `AllowMultiStatement` off
- [ ] `ConfirmAtRiskLevel` set to `Medium` if you want INSERT and UPDATE gated
- [ ] Audit log written somewhere durable and reviewed
- [ ] No auto-approval of tool calls in your client
- [ ] Server not exposed over the network
