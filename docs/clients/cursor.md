# Cursor

AI-first code editor with MCP support.

## Where the config lives

- **Project** - `.cursor/mcp.json` in the repository root
- **Global** - `~/.cursor/mcp.json`

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

Ready to copy: [`examples/cursor.json`](../../examples/cursor.json)

On macOS and Linux use the extensionless binary path instead:
`/path/to/Postgres-mcp/publish/PostgresMcpServer`

## Verify

Open Settings and find the MCP section; the server should be listed with its tools.
Reload the window if it does not appear.

## Notes

- Uses the same `mcpServers` shape as Claude Desktop.
- Project config is per-workspace, so different repositories can point at different
  databases.
