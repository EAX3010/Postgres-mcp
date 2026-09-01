#!/usr/bin/env bash
#
# Builds PostgresMcpServer, prepares its configuration and verifies the setup.
#
# Usage:
#   ./scripts/setup.sh [--output DIR] [--self-contained]
#
set -euo pipefail

OUTPUT_PATH="publish"
SELF_CONTAINED=0

while [ $# -gt 0 ]; do
    case "$1" in
        --output|-o)      OUTPUT_PATH="$2"; shift 2 ;;
        --self-contained) SELF_CONTAINED=1; shift ;;
        --help|-h)
            sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//'
            exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

PROJECT="$REPO_ROOT/PostgresMcpServer.csproj"
case "$OUTPUT_PATH" in
    /*) PUBLISH_DIR="$OUTPUT_PATH" ;;
    *)  PUBLISH_DIR="$REPO_ROOT/$OUTPUT_PATH" ;;
esac
EXE="$PUBLISH_DIR/PostgresMcpServer"
CONFIG="$PUBLISH_DIR/appsettings.json"
TEMPLATE="$REPO_ROOT/appsettings.example.json"

step() { printf '\n\033[36m==> %s\033[0m\n' "$1"; }
warn() { printf '\033[33m%s\033[0m\n' "$1"; }

step "Checking prerequisites"
if ! command -v dotnet >/dev/null 2>&1; then
    printf '\033[31m    .NET SDK not found. Install it from https://dotnet.microsoft.com/download\033[0m\n'
    exit 1
fi
echo "    .NET SDK $(dotnet --version)"

step "Publishing to $PUBLISH_DIR"
PUBLISH_ARGS=("$PROJECT" -c Release -o "$PUBLISH_DIR" --nologo -v q)
if [ "$SELF_CONTAINED" -eq 1 ]; then
    case "$(uname -s)/$(uname -m)" in
        Darwin/arm64) RID="osx-arm64" ;;
        Darwin/*)     RID="osx-x64" ;;
        Linux/aarch64) RID="linux-arm64" ;;
        *)            RID="linux-x64" ;;
    esac
    PUBLISH_ARGS+=(-r "$RID" --self-contained)
    echo "    self-contained for $RID: the .NET runtime is bundled"
fi
dotnet publish "${PUBLISH_ARGS[@]}"
echo "    Published."

step "Configuration"
# The server writes its own starter config, so the template lives in exactly one place.
"$EXE" --init
if [ ! -f "$CONFIG" ]; then
    warn "    --init did not create $CONFIG"
    exit 1
fi
echo "    Every available option is documented in $TEMPLATE"

step "Verifying"
CHECK_RESULT=0
"$EXE" --check || CHECK_RESULT=$?

step "MCP client configuration"
cat <<EOF

Claude Desktop  ->  ~/Library/Application Support/Claude/claude_desktop_config.json
Cursor          ->  ~/.cursor/mcp.json   (or .cursor/mcp.json in a project)
Windsurf        ->  ~/.codeium/windsurf/mcp_config.json
Gemini CLI      ->  ~/.gemini/settings.json

{
  "mcpServers": {
    "postgres": {
      "command": "$EXE"
    }
  }
}

VS Code  ->  .vscode/mcp.json   (note: "servers", and a "type")

{
  "servers": {
    "postgres": {
      "type": "stdio",
      "command": "$EXE"
    }
  }
}

Claude Code:

  claude mcp add postgres --scope user "$EXE"

Per-client detail: docs/clients/
EOF

if [ "$CHECK_RESULT" -ne 0 ]; then
    warn ""
    warn "Setup finished, but --check could not reach every database."
    warn "Edit $CONFIG and run '$EXE --check' again."
    warn "See docs/troubleshooting.md"
    exit 1
fi

printf '\n\033[32mReady. Register the server with your client and restart it.\033[0m\n'
