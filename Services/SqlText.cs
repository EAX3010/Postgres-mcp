using System.Text;
using System.Text.RegularExpressions;

namespace PostgresMcpServer.Services;

/// <summary>
/// Lexical helpers for reasoning about SQL text safely.
///
/// The previous implementation matched raw substrings against the query, which meant
/// comments, string literals and quoted identifiers were all treated as executable
/// keywords (and vice versa). Everything here works against a <see cref="Skeleton"/>:
/// the statement with comments and literal contents blanked out, so keyword matching
/// only ever sees real SQL tokens.
/// </summary>
public static partial class SqlText
{
    /// <summary>
    /// Returns the statement uppercased with comments and literal payloads replaced by
    /// spaces, preserving offsets. Quoted identifiers collapse to opaque 'X' runs so they
    /// still read as a single token but can never match a keyword.
    /// </summary>
    public static string Skeleton(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return string.Empty;

        var sb = new StringBuilder(sql.Length);
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            // -- line comment
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') { sb.Append(' '); i++; }
                continue;
            }

            // /* block comment */ (PostgreSQL allows nesting)
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var depth = 0;
                while (i < sql.Length)
                {
                    if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                    {
                        depth++; sb.Append("  "); i += 2; continue;
                    }
                    if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/')
                    {
                        depth--; sb.Append("  "); i += 2;
                        if (depth == 0) break;
                        continue;
                    }
                    sb.Append(' '); i++;
                }
                continue;
            }

            // 'string literal' with '' escaping
            if (c == '\'')
            {
                sb.Append(' '); i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'') { sb.Append("  "); i += 2; continue; }
                        sb.Append(' '); i++; break;
                    }
                    sb.Append(' '); i++;
                }
                continue;
            }

            // "quoted identifier" with "" escaping
            if (c == '"')
            {
                sb.Append(' '); i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '"')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '"') { sb.Append("XX"); i += 2; continue; }
                        sb.Append(' '); i++; break;
                    }
                    sb.Append('X'); i++;
                }
                continue;
            }

            // $tag$ dollar-quoted string $tag$ (used by DO blocks and function bodies)
            if (c == '$')
            {
                var tag = ReadDollarTag(sql, i);
                if (tag != null)
                {
                    var close = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                    var end = close < 0 ? sql.Length : close + tag.Length;
                    sb.Append(' ', end - i);
                    i = end;
                    continue;
                }
            }

            sb.Append(char.ToUpperInvariant(c));
            i++;
        }

        return sb.ToString();
    }

    /// <summary>Reads a dollar-quote opening tag at <paramref name="start"/>, or null if this is not one (e.g. the $1 of a parameter).</summary>
    private static string? ReadDollarTag(string sql, int start)
    {
        var end = start + 1;
        while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_')) end++;
        if (end >= sql.Length || sql[end] != '$') return null;

        // $1 / $2 are positional parameters, not dollar quotes: a tag may not start with a digit.
        var inner = sql.AsSpan(start + 1, end - start - 1);
        if (inner.Length > 0 && char.IsDigit(inner[0])) return null;

        return sql[start..(end + 1)];
    }

    /// <summary>Number of non-empty statements. Literals and comments cannot contribute separators.</summary>
    public static int StatementCount(string sql) =>
        Skeleton(sql).Split(';').Count(segment => segment.Trim().Length > 0);

    public static bool IsMultiStatement(string sql) => StatementCount(sql) > 1;

    /// <summary>True if <paramref name="keyword"/> appears as a whole token in the executable text.</summary>
    public static bool ContainsKeyword(string sql, string keyword) =>
        HasWord(Skeleton(sql), keyword);

    internal static bool HasWord(string skeleton, string keyword) =>
        Regex.IsMatch(skeleton, $@"(?<![A-Z0-9_]){Regex.Escape(keyword.ToUpperInvariant())}(?![A-Z0-9_])");

    /// <summary>Quotes an identifier for safe interpolation. The result is case-sensitive, as PostgreSQL requires.</summary>
    public static string QuoteIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Identifier must not be empty.", nameof(name));
        if (name.Contains('\0'))
            throw new ArgumentException("Identifier must not contain a null character.", nameof(name));
        if (Encoding.UTF8.GetByteCount(name) > 63)
            throw new ArgumentException($"Identifier '{name}' exceeds PostgreSQL's 63-byte limit.", nameof(name));

        return $"\"{name.Replace("\"", "\"\"")}\"";
    }

    public static string QuoteQualified(string schema, string name) =>
        $"{QuoteIdentifier(schema)}.{QuoteIdentifier(name)}";

    /// <summary>Quotes a string literal. Assumes standard_conforming_strings (PostgreSQL default since 9.1).</summary>
    public static string QuoteLiteral(string value)
    {
        if (value.Contains('\0'))
            throw new ArgumentException("Literal must not contain a null character.", nameof(value));
        return $"'{value.Replace("'", "''")}'";
    }

    /// <summary>Replaces PASSWORD literals so credentials never reach the audit log.</summary>
    public static string RedactSecrets(string sql) =>
        string.IsNullOrEmpty(sql) ? sql : PasswordLiteralRegex().Replace(sql, "PASSWORD '[REDACTED]'");

    [GeneratedRegex(@"\bPASSWORD\s+'(?:[^']|'')*'", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordLiteralRegex();
}
