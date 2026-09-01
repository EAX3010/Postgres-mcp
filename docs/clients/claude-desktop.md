# Claude Desktop

Anthropic's desktop app. It launches stdio MCP servers, which is exactly what this is.

## Where the config lives

- **Windows** - `%APPDATA%\Claude\claude_desktop_config.json`
- **macOS** - `~/Library/Application Support/Claude/claude_desktop_config.json`

Create the file if it does not exist.

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

Ready to copy: [`examples/claude-desktop.json`](../../examples/claude-desktop.json)

On macOS and Linux use the extensionless binary path instead:
`/path/to/Postgres-mcp/publish/PostgresMcpServer`

## Verify

Fully quit Claude Desktop and reopen it, then look for the tools icon in the chat input.
Ask it to run `list_databases`.

## Notes

- Closing the window is **not** enough on Windows; the app keeps running in the tray.
  Right-click the tray icon and choose Quit, then relaunch. Config is read at startup only.
- Backslashes must be doubled in JSON. A single-backslash path fails silently.
- Server logs land in `%APPDATA%\Claude\logs\mcp-server-postgres.log`.
