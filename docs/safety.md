# Safety model

The guarantees rest on PostgreSQL rather than on parsing SQL correctly. That distinction is
the whole design: a classifier can be fooled, a `READ ONLY` transaction cannot.

## What actually protects you

### Read-only is enforced by the server

`query` opens a transaction, issues `SET TRANSACTION READ ONLY`, runs the statement and rolls
back. A write is refused by the database itself, so a statement that slips past classification
still cannot modify data.

The integration suite asserts this by sending an `INSERT` straight past the guard and checking
PostgreSQL rejects it.

### Multi-statement input is refused

One call carries one statement. `SELECT 1; DROP TABLE users` is rejected outright rather than
classified by its first keyword and then executed in full. Batches go through `execute_batch`,
which runs each element as its own command.

Statement counting ignores separators inside string literals, comments and dollar-quoted
bodies, so `SELECT 'a;b'` is correctly one statement.

### Keyword matching ignores comments and literals

Statements are analysed against a *skeleton*: the text with comments and string, dollar-quoted
and identifier bodies blanked out. Consequences:

| Statement | Treated as |
|-----------|-----------|
| `INSERT INTO log VALUES ('user clicked DROP')` | INSERT, not critical |
| `SELECT 1 -- DROP TABLE users` | SELECT, read-only |
| `/*c*/DROP TABLE users` | DROP, confirmation required |
| `;DROP TABLE users` | DROP, confirmation required |
| `WITH t AS (DELETE FROM users ...) SELECT * FROM t` | DELETE, not read-only |
| `DO $$ BEGIN ... END $$` | DO, high risk |
| `SELECT * FROM t WHERE note = 'no limit here'` | read-only, still row-limited |

### Unrecognised statements fail closed

Anything that cannot be classified is treated as a high-risk write requiring confirmation,
rather than waved through as harmless.

### Identifiers are quoted, never interpolated

Table, schema, column and role names are quoted and length-checked; privileges are checked
against an allowlist; passwords are quoted as literals. A table name of
`a; DROP DATABASE prod; --` becomes a single quoted identifier rather than three statements.

Quoting makes identifiers case-sensitive: `create_table(tableName: "MyTable")` creates
`"MyTable"`, not `mytable`.

### EXPLAIN ANALYZE runs inside a rolled-back transaction

`EXPLAIN ANALYZE` genuinely executes its statement. It is run in a transaction that is always
rolled back, so `EXPLAIN ANALYZE DELETE FROM users` returns real timings and loses no rows —
verified in the integration suite. A writing statement still needs `confirm=true`, because
non-transactional side effects such as sequence advances persist.

---

## Risk levels

| Level | Statements |
|-------|-----------|
| **Low** | SELECT, EXPLAIN without ANALYZE, SHOW |
| **Medium** | INSERT, UPDATE with a WHERE clause, CREATE |
| **High** | DROP, ALTER, GRANT, REVOKE, DELETE with a WHERE clause, UPDATE without WHERE, DO blocks, unclassified statements |
| **Critical** | TRUNCATE, DROP DATABASE, DROP SCHEMA, DELETE without WHERE |

Confirmation is required when a statement is at or above `Safety.ConfirmAtRiskLevel`
(default `High`) **or** matches `Safety.CriticalOperations`. Driving it from assessed risk,
not just keyword membership, is what stops a dangerous statement slipping through on a verb
that is not on the list.

WHERE-clause detection runs against the skeleton, so it is unaffected by how the table is
written — `DELETE FROM public.users` and `DELETE FROM "users"` are both recognised as
unqualified deletes.

---

## Dry runs

`dryRun=true` returns the statement, its operation type, risk level and warnings, and executes
nothing:

```
[DRY RUN] Would execute on database 'production':
Operation: DELETE
Risk Level: CRITICAL
Read-only: False
Query: DELETE FROM users
Warnings:
  - Contains critical operation: DELETE
  - DELETE without a WHERE clause removes every row.

To execute this statement, set dryRun to false and confirm to true.
```

Dry runs and refused operations are both written to the audit log — an attempted-but-declined
`DROP DATABASE` is arguably the most interesting thing to record.

---

## What this does **not** guarantee

Be clear about the boundary.

- **It is not access control.** The guards live in a process the assistant is driving. They
  reduce accidents; they are not a security boundary. Database permissions are. See
  [security.md](security.md).
- **A confirmed operation is executed.** `confirm=true` runs it. If the client auto-approves
  tool calls, the confirmation step is worth nothing — which is why the
  [Cline page](clients/cline.md) says to leave `autoApprove` empty.
- **Risk classification is a heuristic.** It fails closed and is covered by tests, but it
  reasons about statement shape, not intent. A perfectly well-formed `DELETE ... WHERE` that
  matches every row is `High`, not `Critical`.
- **`backup` and `restore` shell out.** They run `pg_dump` and `pg_restore` with arguments you
  supply, subject to the file permissions of the account running the server.

## Testing

The unit suite covers classification, WHERE detection, quoting and redaction with no database.
The integration suite starts a throwaway PostgreSQL container and asserts the runtime
guarantees: read-only enforcement, EXPLAIN ANALYZE rollback, transactional batches, audit
redaction and catalog introspection. See [development.md](development.md).
