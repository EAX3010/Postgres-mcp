using System.Text.Json;

namespace PostgresMcpServer.Tools;

/// <summary>Shared serialization for tool responses, including a ceiling on response size.</summary>
internal static class ToolJson
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Serialize(object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value, Options);
        }
        catch (Exception ex)
        {
            // A value Npgsql surfaced could not be represented as JSON; report it rather
            // than letting the exception escape the tool call.
            return $"Error: could not serialize the result ({ex.Message}).";
        }
    }

    /// <summary>
    /// Serializes rows, dropping trailing rows until the payload fits within
    /// <paramref name="maxBytes"/>. Prevents a wide result set from exhausting memory
    /// on the way out even when the row count is small.
    /// </summary>
    public static string SerializeRows(
        Func<IReadOnlyList<Dictionary<string, object?>>, bool, object> build,
        List<Dictionary<string, object?>> rows,
        int maxBytes)
    {
        var included = rows.Count;
        var truncated = false;

        while (true)
        {
            var payload = Serialize(build(rows.Take(included).ToList(), truncated));
            if (System.Text.Encoding.UTF8.GetByteCount(payload) <= maxBytes || included == 0)
                return payload;

            included = included > 1 ? included / 2 : 0;
            truncated = true;
        }
    }
}
