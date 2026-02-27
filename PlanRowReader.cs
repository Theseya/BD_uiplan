using System.Data.Common;

namespace WebApplication1;

/// <summary>
/// Safe reading of plan table row columns when result set may have 36 (workload fallback) or 37 (plan_disciplines + plan_id) columns.
/// Avoids "Reading as System.Int32 is not supported for fields having DataTypeName 'text'" and index out of range.
/// </summary>
public static class PlanRowReader
{
    /// <summary>Parse plan_discipline_id from column 0 (may be int, long, text or DBNull).</summary>
    public static int? SafeParsePlanDisciplineId(object? value)
    {
        if (value is int i) return i;
        if (value is long l) return (int)l;
        if (value == null || value == DBNull.Value) return null;
        return int.TryParse(value.ToString(), out var p) ? p : null;
    }

    /// <summary>Read plan_id from column 36 only when reader has more than 36 columns (main query); otherwise return 0.</summary>
    public static int SafeReadPlanId(DbDataReader r)
    {
        return r.FieldCount > 36 ? SafeReadInt32(r, 36) : 0;
    }

    /// <summary>Read a column that may be int or text/numeric (e.g. course_no, streams_count) as display string.</summary>
    public static string SafeReadIntOrStringAsString(DbDataReader r, int columnIndex)
    {
        if (r.IsDBNull(columnIndex)) return "";
        var v = r.GetValue(columnIndex);
        return v is int i ? i.ToString() : (v?.ToString() ?? "");
    }

    /// <summary>Read a column as int (handles bigint, int, decimal, text from COUNT/aggregates).</summary>
    public static int SafeReadInt32(DbDataReader r, int columnIndex)
    {
        if (r.IsDBNull(columnIndex)) return 0;
        var v = r.GetValue(columnIndex);
        if (v is int i) return i;
        if (v is long l) return (int)l;
        if (v is decimal d) return (int)d;
        if (v != null && int.TryParse(v.ToString(), out var p)) return p;
        return 0;
    }

    /// <summary>Read a column as decimal (handles numeric, text).</summary>
    public static decimal SafeReadDecimal(DbDataReader r, int columnIndex)
    {
        if (r.IsDBNull(columnIndex)) return 0m;
        var v = r.GetValue(columnIndex);
        if (v is decimal d) return d;
        if (v is int i) return i;
        if (v is long l) return l;
        if (v is double dbl) return (decimal)dbl;
        if (v != null && decimal.TryParse(v.ToString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
        return 0m;
    }

    /// <summary>Read a column as bool (handles boolean, text 't'/'f', int 0/1).</summary>
    public static bool SafeReadBoolean(DbDataReader r, int columnIndex)
    {
        if (r.IsDBNull(columnIndex)) return false;
        var v = r.GetValue(columnIndex);
        if (v is bool b) return b;
        if (v is int i) return i != 0;
        var s = v?.ToString()?.Trim();
        return s == "t" || s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "да", StringComparison.OrdinalIgnoreCase);
    }
}
