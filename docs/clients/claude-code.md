# Claude Code

Anthropic's CLI and IDE extension. Registers MCP servers through its own command.

## Where the config lives

Managed by the `claude mcp` command rather than by hand. Project scope writes `.mcp.json`
in the repository root.

## Configuration

```json
{
  "mcpServers": {
    "postgres": {
      "command": "C:\\path\\to\\Postgres-mcp\\publish\\PostgresMcpServer.exe"
    }
  }
}
```

Ready to copy: [`examples/claude-code.mcp.json`](../../examples/claude-code.mcp.json)

On macOS and Linux use the extensionless binary path instead:
`/path/to/Postgres-mcp/publish/PostgresMcpServer`

## Verify

```bash
claude mcp list
```

Or run `/mcp` inside a session.

## Notes

Prefer the CLI over editing files:

```bash
claude mcp add postgres --scope user "C:\path\to\Postgres-mcp\publish\PostgresMcpServer.exe"
```

| Scope | Effect |
|-------|--------|
| `user` | Every project you open. Best for a database tool |
| `local` (default) | Only you, only the current project |
| `project` | Writes `.mcp.json`, shared with anyone who clones the repository |

Committing `.mcp.json` is safe: credentials live in `appsettings.json`, which is gitignored.
