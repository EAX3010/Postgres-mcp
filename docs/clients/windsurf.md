# Windsurf

Codeium's AI editor, with MCP support in Cascade.

## Where the config lives

`~/.codeium/windsurf/mcp_config.json`

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

Ready to copy: [`examples/windsurf.json`](../../examples/windsurf.json)

On macOS and Linux use the extensionless binary path instead:
`/path/to/Postgres-mcp/publish/PostgresMcpServer`

## Verify

Open Cascade, then the MCP panel, and refresh. The server's tools should appear.

## Notes

- Same `mcpServers` shape as Claude Desktop.
- Windsurf caches the server list; use the refresh control after editing the file.
