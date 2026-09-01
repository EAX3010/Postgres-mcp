# Zed

High-performance editor whose assistant supports MCP, which it calls context servers.

## Where the config lives

Zed's `settings.json`, reachable with the `zed: open settings` command.

## Configuration

```json
{
  "context_servers": {
    "postgres": {
      "source": "custom",
      "command": "C:\\path\\to\\Postgres-mcp\\publish\\PostgresMcpServer.exe",
      "args": [],
      "env": {}
    }
  }
}
```

Ready to copy: [`examples/zed.json`](../../examples/zed.json)

On macOS and Linux use the extensionless binary path instead:
`/path/to/Postgres-mcp/publish/PostgresMcpServer`

## Verify

Open the assistant panel; the server appears in its context server list.

## Notes

- Zed uses `context_servers`, not `mcpServers`, and these key names have changed between
  releases. Check Zed's own documentation if this shape is rejected.
