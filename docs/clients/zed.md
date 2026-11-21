# Zed

High-performance editor whose assistant supports MCP, which it calls context servers.

> **The quick way:** run `PostgresMcpServer --setup`. It finds Zed, but Zed's `settings.json` usually contains comments, which rewriting
> would delete - in that case the block to paste is printed with the real path filled in.
> The rest of this page is for doing it by hand.

## Where the config lives

Zed's `settings.json`, reachable with the `zed: open settings` command.

## Configuration

```json
{
  "context_servers": {
    "postgres": {
      "source": "custom",
      "command": "C:\\path\\to\\PostgresMcpServer\\PostgresMcpServer.exe",
      "args": [],
      "env": {}
    }
  }
}
```

Ready to copy: [`examples/zed.json`](../../examples/zed.json)

On macOS and Linux use the extensionless binary path instead:
`/path/to/PostgresMcpServer/PostgresMcpServer`

## Verify

Open the assistant panel; the server appears in its context server list.

## Notes

- Zed uses `context_servers`, not `mcpServers`, and these key names have changed between
  releases. Check Zed's own documentation if this shape is rejected.
