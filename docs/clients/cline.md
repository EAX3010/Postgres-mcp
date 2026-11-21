# Cline

Autonomous coding agent that runs as a VS Code extension.

> **The quick way:** run `PostgresMcpServer --setup`. It finds Cline and adds the server to its settings file for you, with `autoApprove` left empty.
> The rest of this page is for doing it by hand.

## Where the config lives

Managed through the extension UI. The underlying file is `cline_mcp_settings.json` in the
extension's storage directory - reach it via the MCP Servers icon, then Configure.

## Configuration

```json
{
  "mcpServers": {
    "postgres": {
      "command": "C:\\path\\to\\PostgresMcpServer\\PostgresMcpServer.exe",
      "disabled": false,
      "autoApprove": []
    }
  }
}
```

Ready to copy: [`examples/cline.json`](../../examples/cline.json)

On macOS and Linux use the extensionless binary path instead:
`/path/to/PostgresMcpServer/PostgresMcpServer`

## Verify

The MCP Servers panel shows the server with a green indicator and lists its tools.

## Notes

- Leave `autoApprove` empty. Auto-approving a tool that can `DROP TABLE` removes the
  confirmation step this server is built around.
- `disabled: false` is required; Cline adds the key itself when you toggle a server.
