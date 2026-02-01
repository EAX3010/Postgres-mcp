# PostgreSQL MCP Server

A Model Context Protocol (MCP) server that enables AI assistants to interact with PostgreSQL databases through a comprehensive set of database management tools.

## Features

- **Query Execution** - Run SELECT queries with automatic result limiting and execution plans
- **Schema Exploration** - Browse schemas, tables, columns, indexes, and constraints
- **Data Modification** - Execute INSERT, UPDATE, DELETE with safety checks
- **Administration** - Create tables, manage roles, grant privileges
- **Backup & Restore** - Full database backups using pg_dump/pg_restore
- **Safety Guards** - Risk assessment, dry-run mode, confirmation prompts for critical operations
- **Audit Logging** - Track all operations with timestamps and affected rows
- **Multi-Instance Support** - Safe concurrent usage from multiple Claude instances

## Requirements

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) or later
- PostgreSQL server (tested with PostgreSQL 14+)
- `pg_dump` and `pg_restore` in PATH (for backup/restore features)

## Installation

### 1. Clone the repository

```bash
git clone https://github.com/yourusername/postgres-mcp-server.git
cd postgres-mcp-server
```

### 2. Configure database connections

Copy the example configuration and add your database credentials:

```bash
cp appsettings.example.json appsettings.json
```

Edit `appsettings.json` with your database connection strings:

```json
{
  "Databases": {
    "production": "Host=localhost;Port=5432;Database=mydb;Username=postgres;Password=YOUR_PASSWORD",
    "development": "Host=localhost;Port=5432;Database=devdb;Username=dev_user;Password=YOUR_PASSWORD"
  },
  "Safety": {
    "RequireConfirmation": true,
    "EnableDryRun": true,
    "CriticalOperations": ["DROP", "TRUNCATE", "DELETE", "ALTER", "GRANT", "REVOKE"]
  },
  "Audit": {
    "Enabled": true,
    "LogPath": "audit.log",
    "LogToConsole": false
  }
}
```

### 3. Build the project

```bash
dotnet build
```

## Configuration

### Environment Variables

You can override any configuration using environment variables with the `POSTGRES_MCP_` prefix:

```bash
# Override a database connection
export POSTGRES_MCP_Databases__production="Host=prod.example.com;..."

# Disable dry-run mode
export POSTGRES_MCP_Safety__EnableDryRun=false

# Change audit log path
export POSTGRES_MCP_Audit__LogPath="/var/log/postgres-mcp/audit.log"
```

### Claude Desktop Integration

Add to your Claude Desktop configuration (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "postgres": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/PostgresMcpServer.csproj"],
      "env": {
        "POSTGRES_MCP_Databases__production": "Host=localhost;Port=5432;Database=mydb;Username=postgres;Password=YOUR_PASSWORD",
        "POSTGRES_MCP_Databases__development": "Host=localhost;Port=5432;Database=devdb;Username=dev_user;Password=YOUR_PASSWORD",
        "POSTGRES_MCP_Safety__RequireConfirmation": "true",
        "POSTGRES_MCP_Safety__EnableDryRun": "true",
        "POSTGRES_MCP_Audit__Enabled": "true"
      }
    }
  }
}
```

For compiled executable (recommended - faster startup):

```json
{
  "mcpServers": {
    "postgres": {
      "command": "/path/to/bin/Debug/net10.0/PostgresMcpServer.exe",
      "cwd": "/path/to/bin/Debug/net10.0",
      "env": {
        "POSTGRES_MCP_Databases__mydb": "Host=localhost;Port=5432;Database=mydb;Username=postgres;Password=YOUR_PASSWORD"
      }
    }
  }
}
```

### Claude Code Integration

Add to your project's `.mcp.json`:

```json
{
  "mcpServers": {
    "postgres": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/PostgresMcpServer.csproj", "--no-build"],
      "env": {
        "POSTGRES_MCP_Databases__mydb": "Host=localhost;Port=5432;Database=mydb;Username=postgres;Password=YOUR_PASSWORD"
      }
    }
  }
}
```

## Available Tools

### Query Tools

| Tool | Description |
|------|-------------|
| `query` | Execute SELECT queries with automatic LIMIT |
| `list_databases` | List all configured database connections |
| `explain` | Get query execution plan (with optional ANALYZE) |

### Schema Tools

| Tool | Description |
|------|-------------|
| `list_schemas` | List all schemas in a database |
| `list_tables` | List tables with optional schema filter |
| `describe_table` | Get detailed table metadata |
| `get_table_ddl` | Generate CREATE TABLE DDL statement |

### Execute Tools

| Tool | Description |
|------|-------------|
| `execute` | Run INSERT/UPDATE/DELETE with safety checks |
| `execute_batch` | Run multiple statements in a transaction |

### Admin Tools

| Tool | Description |
|------|-------------|
| `create_table` | Create a new table |
| `drop_table` | Drop a table (with CASCADE option) |
| `list_roles` | List database roles and permissions |
| `create_role` | Create a new database role |
| `grant_privileges` | Grant table privileges to a role |
| `get_database_stats` | Get database health metrics |

### Backup Tools

| Tool | Description |
|------|-------------|
| `backup` | Create database backup using pg_dump |
| `restore` | Restore database from backup |

## Safety Features

### Risk Assessment

Every write operation is analyzed for risk level:

- **Low** - Standard INSERT/UPDATE with WHERE clause
- **Medium** - Bulk operations, ALTER statements
- **High** - DELETE without WHERE, schema changes
- **Critical** - DROP DATABASE, TRUNCATE, mass DELETE

### Dry-Run Mode

When enabled, write operations show what would happen without executing:

```
[DRY RUN] Would execute: DELETE FROM users WHERE status = 'inactive'
Estimated rows affected: 42
Risk level: medium
```

### Confirmation Prompts

Critical operations require explicit confirmation before execution.

## Multi-Instance Support

This server supports multiple concurrent Claude instances safely:

- Each instance gets a unique 8-character ID
- Audit logs include instance IDs for traceability
- File-level locking prevents log corruption
- Connection pooling handles concurrent database access

Example audit log entry:
```json
{"Timestamp":"2025-01-15T10:30:00Z","InstanceId":"a1b2c3d4","Database":"production","Operation":"query","Query":"SELECT * FROM users","User":"john","DryRun":false,"Success":true,"RowsAffected":100}
```

## Security Best Practices

1. **Never commit credentials** - Use environment variables or secrets management
2. **Use read-only users** - Create database users with minimal required permissions
3. **Enable audit logging** - Track all operations for compliance
4. **Keep dry-run enabled** - Prevent accidental data modifications
5. **Review critical operations** - Always verify before confirming destructive actions

### Creating a Read-Only Database User

```sql
CREATE ROLE mcp_readonly WITH LOGIN PASSWORD 'secure_password';
GRANT CONNECT ON DATABASE mydb TO mcp_readonly;
GRANT USAGE ON SCHEMA public TO mcp_readonly;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO mcp_readonly;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO mcp_readonly;
```

## Development

### Project Structure

```
├── Models/              # Data models
│   ├── AuditEntry.cs
│   ├── DatabaseConfig.cs
│   └── QueryResult.cs
├── Services/            # Core business logic
│   ├── AuditLogger.cs
│   ├── ConnectionManager.cs
│   ├── PostgresService.cs
│   └── SafetyGuard.cs
├── Tools/               # MCP tool definitions
│   ├── AdminTools.cs
│   ├── BackupTools.cs
│   ├── ExecuteTools.cs
│   ├── QueryTools.cs
│   └── SchemaTools.cs
├── Program.cs           # Application entry point
└── appsettings.example.json
```

### Building for Production

```bash
dotnet publish -c Release -o ./publish
```

### Running Tests

```bash
dotnet test
```

## Troubleshooting

### Connection Issues

1. Verify PostgreSQL is running: `pg_isready -h localhost -p 5432`
2. Check connection string format in configuration
3. Ensure firewall allows connections on PostgreSQL port

### Permission Errors

1. Verify database user has required permissions
2. Check `pg_hba.conf` allows connections from your host
3. Review audit logs for specific error messages

### Backup/Restore Failures

1. Ensure `pg_dump` and `pg_restore` are in PATH
2. Verify PostgreSQL version compatibility
3. Check disk space for backup files

## License

MIT License - see [LICENSE](LICENSE) file for details.

## Contributing

Contributions are welcome! Please read our contributing guidelines before submitting pull requests.

1. Fork the repository
2. Create a feature branch
3. Commit your changes
4. Push to the branch
5. Open a pull request
