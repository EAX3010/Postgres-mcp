namespace PostgresMcpServer.Models;

public class QueryResult
{
    public bool Success { get; set; }
    public List<Dictionary<string, object?>> Rows { get; set; } = [];
    public int RowsAffected { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Columns { get; set; } = [];
    public TimeSpan ExecutionTime { get; set; }

    /// <summary>True when reading stopped at the row cap rather than at the end of the result set.</summary>
    public bool Truncated { get; set; }
}

public class BatchStatementResult
{
    public int Index { get; set; }
    public string Sql { get; set; } = string.Empty;
    public int RowsAffected { get; set; }
}

public class BatchResult
{
    public bool Success { get; set; }
    public bool RolledBack { get; set; }
    public string? ErrorMessage { get; set; }
    public int? FailedStatementIndex { get; set; }
    public List<BatchStatementResult> Statements { get; set; } = [];
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

    /// <summary>Full type including modifiers, e.g. character varying(255) or numeric(10,2).</summary>
    public string DataType { get; set; } = string.Empty;
    public bool IsNullable { get; set; }
    public string? DefaultValue { get; set; }
    public bool IsPrimaryKey { get; set; }
    public int OrdinalPosition { get; set; }

    /// <summary>GENERATED ... AS IDENTITY marker: 'a' (always), 'd' (by default), or null.</summary>
    public string? Identity { get; set; }

    /// <summary>Expression for a GENERATED ALWAYS AS (...) STORED column.</summary>
    public string? GeneratedExpression { get; set; }
    public string? Collation { get; set; }
}

public class IndexInfo
{
    public string Name { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = [];
    public bool IsUnique { get; set; }
    public bool IsPrimary { get; set; }

    /// <summary>The server's own CREATE INDEX statement, which round-trips exactly.</summary>
    public string Definition { get; set; } = string.Empty;
}

public class ConstraintInfo
{
    public string Name { get; set; } = string.Empty;

    /// <summary>PRIMARY KEY, FOREIGN KEY, UNIQUE, CHECK or EXCLUSION.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The server's own constraint definition, which round-trips exactly.</summary>
    public string Definition { get; set; } = string.Empty;
}
