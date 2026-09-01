# Tools

Seventeen tools across five groups. Every one takes `database` — the connection name from your
[configuration](configuration.md) — as its first argument.

| Group | Tools |
|-------|-------|
| [Query](#query-tools) | `query`, `list_databases`, `explain` |
| [Schema](#schema-tools) | `list_schemas`, `list_tables`, `describe_table`, `get_table_ddl` |
| [Execute](#execute-tools) | `execute`, `execute_batch` |
| [Admin](#admin-tools) | `create_table`, `drop_table`, `list_roles`, `create_role`, `grant_privileges`, `get_database_stats` |
| [Backup](#backup-tools) | `backup`, `restore` |

Write tools share two arguments: `dryRun` previews without executing, and `confirm`
authorises an operation the [safety guard](safety.md) flagged.

---

## Query tools

### `query`

Runs one SELECT inside a `READ ONLY` transaction that is always rolled back.

| Argument | Default | Description |
|----------|---------|-------------|
| `database` | — | Connection name |
| `sql` | — | A single SELECT statement |
| `limit` | `100` | Rows to return, capped by `Limits.MaxRows` |

Returns `rowCount`, `columns`, `rows`, `truncated` and `executionTime`. `truncated` is true
when the row cap or the response-size cap stopped it early.

Rejected: multi-statement input, and anything not read-only. Writes are refused by PostgreSQL
itself, not merely by inspection.

### `list_databases`

No arguments. Lists configured connection names, plus a `misconfigured` map naming any whose
connection string failed to parse. The quickest check that the server is alive — it needs no
working database connection.

### `explain`

| Argument | Default | Description |
|----------|---------|-------------|
| `database` | — | Connection name |
| `sql` | — | Statement to analyse |
| `analyze` | `false` | Run `EXPLAIN ANALYZE`, which really executes the statement |
| `confirm` | `false` | Required for `analyze=true` on a writing statement |

With `analyze=true` the statement runs inside a transaction that is always rolled back, so
`EXPLAIN ANALYZE DELETE FROM users` returns real timings without losing rows. Side effects
outside transaction control — sequence advances, for instance — still happen, which is why a
writing statement needs `confirm`.

---

## Schema tools

### `list_schemas`

Non-system schemas in the database.

### `list_tables`

| Argument | Default | Description |
|----------|---------|-------------|
| `schema` | all | Restrict to one schema |

### `describe_table`

| Argument | Default |
|----------|---------|
| `table` | — |
| `schema` | `public` |

Returns columns (name, full type with modifiers, nullability, default, primary key, identity,
generated expression, collation), indexes (with the server's own `CREATE INDEX` text) and
constraints (with `pg_get_constraintdef` output), plus an estimated row count from
`pg_class.reltuples`.

### `get_table_ddl`

Same arguments. Reconstructs a `CREATE TABLE` from `pg_catalog`, so type modifiers
(`varchar(255)`, `numeric(10,2)`), identity columns, generated columns, collations, foreign
keys, checks, unique constraints and expression or partial indexes all survive. Identifiers
are quoted.

---

## Execute tools

### `execute`

One INSERT, UPDATE, DELETE or DDL statement.

| Argument | Default | Description |
|----------|---------|-------------|
| `sql` | — | A single statement |
| `dryRun` | `false` | Preview: operation, risk level, warnings; executes nothing |
| `confirm` | `false` | Authorise an operation the guard flagged |

Returns `rowsAffected`, `operation`, `riskLevel` and `executionTime`. Read-only statements are
refused — use `query`.

When confirmation is required the call returns a `requiresConfirmation` object describing the
risk instead of executing. Re-send with `confirm=true`.

### `execute_batch`

| Argument | Default | Description |
|----------|---------|-------------|
| `statements` | — | Array of statements, **one statement per element** |
| `dryRun` | `false` | Preview every statement with its risk level |
| `confirm` | `false` | Authorise, if any statement is risky |

Runs each element as its own command inside one real transaction. Every statement commits or
none does. Returns per-statement `rowsAffected`; on failure returns `failedStatementIndex`
and confirms the rollback.

---

## Admin tools

### `create_table`

| Argument | Default |
|----------|---------|
| `tableName` | — |
| `columns` | — (JSON array) |
| `schema` | `public` |

```json
[
  { "name": "id",         "type": "integer",       "primaryKey": true },
  { "name": "email",      "type": "varchar(255)",  "nullable": false },
  { "name": "balance",    "type": "numeric(10,2)", "default": "0" },
  { "name": "created_at", "type": "timestamptz",   "default": "now()" }
]
```

Names are quoted, types are validated against a conservative pattern, and a `default`
containing more than one statement is rejected.

### `drop_table`

| Argument | Default | Description |
|----------|---------|-------------|
| `tableName` | — | |
| `schema` | `public` | |
| `cascade` | `false` | Also drop dependent objects |

Always requires `confirm=true`.

### `list_roles`

Roles with `is_superuser`, `can_create_roles`, `can_create_db`, `can_login` and
`connection_limit`. Internal `pg_*` roles are excluded.

### `create_role`

| Argument | Default |
|----------|---------|
| `roleName` | — |
| `password` | none |
| `canLogin` | `true` |
| `canCreateDb` | `false` |
| `canCreateRoles` | `false` |

The password is quoted as a literal, so one containing `'` is handled correctly, and it is
redacted before the statement reaches the audit log.

### `grant_privileges`

| Argument | Default |
|----------|---------|
| `tableName` | — |
| `roleName` | — |
| `privileges` | `SELECT` |
| `schema` | `public` |

Comma-separated. Checked against an allowlist: `SELECT`, `INSERT`, `UPDATE`, `DELETE`,
`TRUNCATE`, `REFERENCES`, `TRIGGER`, `MAINTAIN`, `ALL`, `ALL PRIVILEGES`.

### `get_database_stats`

Size, active connections, committed and rolled-back transactions, blocks read, buffer hits
and cache hit ratio for the current database.

---

## Backup tools

Both need `pg_dump` / `pg_restore` on `PATH`. The password is passed to the child process
through `PGPASSWORD`; `sslmode`, root certificate and connect timeout are carried across from
the connection string.

### `backup`

| Argument | Default | Description |
|----------|---------|-------------|
| `outputPath` | — | Destination file |
| `format` | `custom` | `plain`, `custom`, `directory` or `tar` |
| `schemaOnly` | `false` | Structure only |
| `dataOnly` | `false` | Data only |
| `tables` | all | Comma-separated list |

### `restore`

| Argument | Default | Description |
|----------|---------|-------------|
| `inputPath` | — | Backup file |
| `clean` | `false` | Drop objects before recreating them |
| `createDb` | `false` | Create the database first |

Success is determined by exit code alone. Warnings on stderr are returned in a `warnings`
field, never mistaken for failure — nor a failure for success. Cancelling the tool call kills
the process tree, so a cancelled restore does not keep writing.
