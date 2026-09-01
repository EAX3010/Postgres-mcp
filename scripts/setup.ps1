#Requires -Version 5.1
<#
.SYNOPSIS
    Builds PostgresMcpServer, prepares its configuration and verifies the setup.

.DESCRIPTION
    Publishes to ./publish, creates a starter appsettings.json via --init if none exists,
    runs the built-in --check diagnostic, and prints the MCP client configuration block with
    the real path filled in.

.PARAMETER OutputPath
    Publish directory. Defaults to ./publish.

.PARAMETER SelfContained
    Bundle the .NET runtime so the machine running it does not need .NET installed.

.EXAMPLE
    .\scripts\setup.ps1
.EXAMPLE
    .\scripts\setup.ps1 -SelfContained
#>
[CmdletBinding()]
param(
    [string]$OutputPath = "publish",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$project = Join-Path $repoRoot "PostgresMcpServer.csproj"
$publishDir = if ([System.IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $repoRoot $OutputPath }
$exe = Join-Path $publishDir "PostgresMcpServer.exe"
$config = Join-Path $publishDir "appsettings.json"
$template = Join-Path $repoRoot "appsettings.example.json"

function Write-Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

Write-Step "Checking prerequisites"
try {
    $sdk = (dotnet --version).Trim()
    Write-Host "    .NET SDK $sdk"
}
catch {
    Write-Host "    .NET SDK not found. Install it from https://dotnet.microsoft.com/download" -ForegroundColor Red
    exit 1
}

Write-Step "Publishing to $publishDir"
$publishArgs = @($project, "-c", "Release", "-o", $publishDir, "--nologo", "-v", "q")
if ($SelfContained) {
    $publishArgs += @("-r", "win-x64", "--self-contained")
    Write-Host "    self-contained: the .NET runtime is bundled"
}
dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { Write-Host "    Publish failed." -ForegroundColor Red; exit 1 }
Write-Host "    Published."

Write-Step "Configuration"
# The server writes its own starter config, so the template lives in exactly one place.
& $exe --init
if (-not (Test-Path $config)) {
    Write-Host "    --init did not create $config" -ForegroundColor Red
    exit 1
}
Write-Host "    Every available option is documented in $template"

Write-Step "Verifying"
& $exe --check
$checkResult = $LASTEXITCODE

Write-Step "MCP client configuration"
$escaped = $exe -replace '\\', '\\'
Write-Host @"

Claude Desktop  ->  %APPDATA%\Claude\claude_desktop_config.json
Cursor          ->  ~/.cursor/mcp.json   (or .cursor/mcp.json in a project)
Windsurf        ->  ~/.codeium/windsurf/mcp_config.json
Gemini CLI      ->  ~/.gemini/settings.json

{
  "mcpServers": {
    "postgres": {
      "command": "$escaped"
    }
  }
}

VS Code  ->  .vscode/mcp.json   (note: "servers", and a "type")

{
  "servers": {
    "postgres": {
      "type": "stdio",
      "command": "$escaped"
    }
  }
}

Claude Code:

  claude mcp add postgres --scope user "$exe"

Per-client detail: docs/clients/
"@

if ($checkResult -ne 0) {
    Write-Host "`nSetup finished, but --check could not reach every database." -ForegroundColor Yellow
    Write-Host "Edit $config and run '$exe --check' again." -ForegroundColor Yellow
    Write-Host "See docs/troubleshooting.md" -ForegroundColor Yellow
    exit 1
}

Write-Host "`nReady. Register the server with your client and restart it." -ForegroundColor Green
