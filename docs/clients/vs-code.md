# VS Code (GitHub Copilot)

Copilot agent mode in VS Code can call MCP tools.

> **The quick way:** run `PostgresMcpServer --setup`. It finds VS Code and adds the server to your **user** `mcp.json`. If that file contains
> comments it is left alone, and the block to paste is printed instead.
> The rest of this page is for doing it by hand.

## Where the config lives

- **Workspace** - `.vscode/mcp.json`
- **User** - Command Palette, `MCP: Open User Configuration`

## Configuration

```json
{
  "servers": {
    "postgres": {
      "type": "stdio",
      "command": "C:\\path\\to\\PostgresMcpServer\\PostgresMcpServer.exe"
    }
  }
}
```

Ready to copy: [`examples/vs-code.json`](../../examples/vs-code.json)

On macOS and Linux use the extensionless binary path instead:
`/path/to/PostgresMcpServer/PostgresMcpServer`

## Verify

Switch Copilot Chat to **Agent** mode and open the tools picker; `postgres` should be listed.

## Notes

- VS Code uses `servers`, **not** `mcpServers`, and wants an explicit `"type": "stdio"`.
  Copying a Claude Desktop block verbatim will not work.
- The Command Palette command `MCP: Add Server` writes this file for you.
