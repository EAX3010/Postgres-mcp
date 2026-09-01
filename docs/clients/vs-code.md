# VS Code (GitHub Copilot)

Copilot agent mode in VS Code can call MCP tools.

## Where the config lives

- **Workspace** - `.vscode/mcp.json`
- **User** - Command Palette, `MCP: Open User Configuration`

## Configuration

```json
{
  "servers": {
    "postgres": {
      "type": "stdio",
      "command": "C:\\path\\to\\Postgres-mcp\\publish\\PostgresMcpServer.exe"
    }
  }
}
```

Ready to copy: [`examples/vs-code.json`](../../examples/vs-code.json)

On macOS and Linux use the extensionless binary path instead:
`/path/to/Postgres-mcp/publish/PostgresMcpServer`

## Verify

Switch Copilot Chat to **Agent** mode and open the tools picker; `postgres` should be listed.

## Notes

- VS Code uses `servers`, **not** `mcpServers`, and wants an explicit `"type": "stdio"`.
  Copying a Claude Desktop block verbatim will not work.
- The Command Palette command `MCP: Add Server` writes this file for you.
