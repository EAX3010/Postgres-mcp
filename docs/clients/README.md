# MCP client setup

Pick your client. Every page follows the same shape: where the config file lives, the block to
paste, how to verify, and the gotcha specific to that client.

| Client | Config key | Page | Example |
|--------|-----------|------|---------|
| Claude Desktop | `mcpServers` | [claude-desktop.md](claude-desktop.md) | [claude-desktop.json](../../examples/claude-desktop.json) |
| Claude Code | *(managed by CLI)* | [claude-code.md](claude-code.md) | [claude-code.mcp.json](../../examples/claude-code.mcp.json) |
| Cursor | `mcpServers` | [cursor.md](cursor.md) | [cursor.json](../../examples/cursor.json) |
| VS Code (Copilot) | `servers` | [vs-code.md](vs-code.md) | [vs-code.json](../../examples/vs-code.json) |
| Windsurf | `mcpServers` | [windsurf.md](windsurf.md) | [windsurf.json](../../examples/windsurf.json) |
| Cline | `mcpServers` | [cline.md](cline.md) | [cline.json](../../examples/cline.json) |
| Gemini CLI | `mcpServers` | [gemini-cli.md](gemini-cli.md) | [gemini-cli.json](../../examples/gemini-cli.json) |
| Zed | `context_servers` | [zed.md](zed.md) | [zed.json](../../examples/zed.json) |
| ChatGPT / OpenAI | *not supported* | [chatgpt.md](chatgpt.md) | — |

## The canonical block

Most clients converged on the same shape. All you supply is the absolute path to the
executable — no arguments, and no `cwd`, because the server resolves its config against its
own directory.

```json
{
  "mcpServers": {
    "postgres": {
      "command": "C:\\path\\to\\Postgres-mcp\\publish\\PostgresMcpServer.exe"
    }
  }
}
```

Two clients deviate, and both are easy to get wrong:

- **VS Code** uses `servers` instead of `mcpServers`, and wants `"type": "stdio"`.
- **Zed** uses `context_servers`.

## Passing credentials through the environment

Every client that accepts an `env` block can supply the database connection directly, with no
`appsettings.json` at all:

```json
{
  "mcpServers": {
    "postgres": {
      "command": "C:\\path\\to\\Postgres-mcp\\publish\\PostgresMcpServer.exe",
      "env": {
        "POSTGRESMCP_Databases__local": "Host=localhost;Port=5432;Database=myapp;Username=mcp_readonly;Password=..."
      }
    }
  }
}
```

Full example: [`examples/env-only.json`](../../examples/env-only.json). See
[configuration.md](../configuration.md) for the naming rules.

## Before you blame the client

Run the server's own diagnostic first. It is far more informative than a client's
"server failed to start":

```bash
./PostgresMcpServer --check
```

That validates the config and actually connects to every database. If `--check` passes and
the client still shows nothing, the problem is the client registration — see
[troubleshooting.md](../troubleshooting.md).

## Common mistakes

1. **Single backslashes on Windows.** JSON requires `\\`. This is the single most common
   cause of a config that silently does nothing.
2. **A relative path to the executable.** Clients do not all resolve them the same way. Use
   an absolute path.
3. **Not restarting the client.** Every one of these reads its config at startup.
4. **Editing the wrong file.** Several clients have both a user-level and a project-level
   config; check the page for yours.

## A note on accuracy

Client config formats and file locations change between releases. The blocks here were
correct when written, but if a client rejects one, its own documentation is authoritative.
The server side never changes: an absolute path to the executable, optionally an `env` block.
