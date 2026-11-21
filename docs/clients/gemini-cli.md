# Gemini CLI

Google's terminal agent, which supports MCP servers.

> **The quick way:** run `PostgresMcpServer --setup`. It finds Gemini CLI and adds the server to the file below for you.
> The rest of this page is for doing it by hand.

## Where the config lives

`~/.gemini/settings.json`

## Configuration

```json
{
  "mcpServers": {
    "postgres": {
      "command": "C:\\path\\to\\PostgresMcpServer\\PostgresMcpServer.exe"
    }
  }
}
```

Ready to copy: [`examples/gemini-cli.json`](../../examples/gemini-cli.json)

On macOS and Linux use the extensionless binary path instead:
`/path/to/PostgresMcpServer/PostgresMcpServer`

## Verify

Run `/mcp` inside the CLI to list configured servers and their tools.

## Notes

- Merge the `mcpServers` block into the existing settings file rather than replacing it.
- Same shape as Claude Desktop.
