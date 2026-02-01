namespace PostgresMcpServer.Models;

public class QueryResult
{
    public bool Success { get; set; }
    public List<Dictionary<string, object?>> Rows { get; set; } = [];
    public int RowsAffected { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Columns { get; set; } = [];
    public TimeSpan ExecutionTime { get; set; }
}

public class SchemaInfo
{
    public string SchemaName { get; set; } = string.Empty;
    public List<TableInfo> Tables { get; set; } = [];
}

public class TableInfo
{
    public string TableName { get; set; } = string.Empty;
    public string SchemaName { get; set; } = "public";
    public List<ColumnInfo> Columns { get; set; } = [];
    public List<IndexInfo> Indexes { get; set; } = [];
    public List<ConstraintInfo> Constraints { get; set; } = [];
    public long? RowCount { get; set; }
}

public class ColumnInfo
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool IsNullable { get; set; }
    public string? DefaultValue { get; set; }
    public bool IsPrimaryKey { get; set; }
    public int OrdinalPosition { get; set; }
}

public class IndexInfo
{
    public string Name { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = [];
    public bool IsUnique { get; set; }
    public bool IsPrimary { get; set; }
}

public class ConstraintInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // PRIMARY KEY, FOREIGN KEY, UNIQUE, CHECK
    public List<string> Columns { get; set; } = [];
    public string? ReferencesTable { get; set; }
    public List<string>? ReferencesColumns { get; set; }
}
