using System.Data;
using WebApplication1;
using Xunit;

namespace WebApplication1.Tests;

/// <summary>
/// Tests for safe reading of plan table rows (36 vs 37 columns, text/numeric vs int).
/// </summary>
public class PlanRowReaderTests
{
    [Theory]
    [InlineData(42, 42)]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    public void SafeParsePlanDisciplineId_Int_ReturnsValue(int value, int expected)
    {
        Assert.Equal(expected, PlanRowReader.SafeParsePlanDisciplineId(value));
    }

    [Fact]
    public void SafeParsePlanDisciplineId_Long_ConvertsToInt()
    {
        Assert.Equal(100, PlanRowReader.SafeParsePlanDisciplineId((long)100));
    }

    [Fact]
    public void SafeParsePlanDisciplineId_Null_ReturnsNull()
    {
        Assert.Null(PlanRowReader.SafeParsePlanDisciplineId(null));
    }

    [Fact]
    public void SafeParsePlanDisciplineId_DBNull_ReturnsNull()
    {
        Assert.Null(PlanRowReader.SafeParsePlanDisciplineId(DBNull.Value));
    }

    [Fact]
    public void SafeParsePlanDisciplineId_StringNumeric_Parses()
    {
        Assert.Equal(123, PlanRowReader.SafeParsePlanDisciplineId("123"));
        Assert.Equal(0, PlanRowReader.SafeParsePlanDisciplineId("0"));
    }

    [Fact]
    public void SafeParsePlanDisciplineId_StringNonNumeric_ReturnsNull()
    {
        Assert.Null(PlanRowReader.SafeParsePlanDisciplineId("abc"));
        Assert.Null(PlanRowReader.SafeParsePlanDisciplineId(""));
    }

    [Fact]
    public void SafeReadPlanId_WhenReaderHas36Columns_ReturnsZero_WithoutAccessingColumn36()
    {
        var table = new DataTable();
        for (int i = 0; i < 36; i++)
            table.Columns.Add("c" + i, i == 0 ? typeof(int) : typeof(string));
        table.Rows.Add(Enumerable.Range(0, 36).Select(i => i == 0 ? (object)99 : (object)DBNull.Value).ToArray());
        using var reader = table.CreateDataReader();
        reader.Read();
        var planId = PlanRowReader.SafeReadPlanId(reader);
        Assert.Equal(0, planId);
    }

    [Fact]
    public void SafeReadPlanId_WhenReaderHas37Columns_AndColumn36IsInt_ReturnsValue()
    {
        var table = new DataTable();
        for (int i = 0; i < 37; i++)
            table.Columns.Add("c" + i, typeof(int));
        table.Rows.Add(Enumerable.Range(0, 37).Select(i => (object)i).ToArray());
        using var reader = table.CreateDataReader();
        reader.Read();
        var planId = PlanRowReader.SafeReadPlanId(reader);
        Assert.Equal(36, planId);
    }

    [Fact]
    public void SafeReadPlanId_WhenReaderHas37Columns_Column36Null_ReturnsZero()
    {
        var table = new DataTable();
        for (int i = 0; i < 37; i++)
            table.Columns.Add("c" + i, typeof(int));
        table.Rows.Add(Enumerable.Range(0, 36).Select(i => (object)i).Concat(new object[] { DBNull.Value }).ToArray());
        using var reader = table.CreateDataReader();
        reader.Read();
        var planId = PlanRowReader.SafeReadPlanId(reader);
        Assert.Equal(0, planId);
    }

    [Fact]
    public void SafeReadPlanId_WhenReaderHas38Columns_ReadsPlanIdFromColumn37()
    {
        var table = new DataTable();
        for (int i = 0; i < 38; i++)
            table.Columns.Add("c" + i, typeof(int));
        table.Rows.Add(Enumerable.Range(0, 37).Select(i => (object)i).Concat(new object[] { 99 }).ToArray());
        using var reader = table.CreateDataReader();
        reader.Read();
        var planId = PlanRowReader.SafeReadPlanId(reader);
        Assert.Equal(99, planId);
    }

    [Fact]
    public void SafeReadIntOrStringAsString_IntColumn_ReturnsString()
    {
        var table = new DataTable();
        table.Columns.Add("col", typeof(int));
        table.Rows.Add(42);
        using var reader = table.CreateDataReader();
        reader.Read();
        Assert.Equal("42", PlanRowReader.SafeReadIntOrStringAsString(reader, 0));
    }

    [Fact]
    public void SafeReadIntOrStringAsString_StringColumn_ReturnsString()
    {
        var table = new DataTable();
        table.Columns.Add("col", typeof(string));
        table.Rows.Add("2");
        using var reader = table.CreateDataReader();
        reader.Read();
        Assert.Equal("2", PlanRowReader.SafeReadIntOrStringAsString(reader, 0));
    }

    [Fact]
    public void SafeReadIntOrStringAsString_DBNull_ReturnsEmpty()
    {
        var table = new DataTable();
        table.Columns.Add("col", typeof(int));
        table.Rows.Add(DBNull.Value);
        using var reader = table.CreateDataReader();
        reader.Read();
        Assert.Equal("", PlanRowReader.SafeReadIntOrStringAsString(reader, 0));
    }

    [Fact]
    public void SafeReadIntOrStringAsString_DecimalColumn_ReturnsToString()
    {
        var table = new DataTable();
        table.Columns.Add("col", typeof(decimal));
        table.Rows.Add(10m);
        using var reader = table.CreateDataReader();
        reader.Read();
        Assert.Equal("10", PlanRowReader.SafeReadIntOrStringAsString(reader, 0));
    }

    [Fact]
    public void SafeReadInt32_IntColumn_ReturnsValue()
    {
        var table = new DataTable();
        table.Columns.Add("col", typeof(int));
        table.Rows.Add(42);
        using var reader = table.CreateDataReader();
        reader.Read();
        Assert.Equal(42, PlanRowReader.SafeReadInt32(reader, 0));
    }

    [Fact]
    public void SafeReadInt32_LongColumn_ConvertsToInt()
    {
        var table = new DataTable();
        table.Columns.Add("col", typeof(long));
        table.Rows.Add(1000L);
        using var reader = table.CreateDataReader();
        reader.Read();
        Assert.Equal(1000, PlanRowReader.SafeReadInt32(reader, 0));
    }

    [Fact]
    public void SafeReadInt32_StringNumeric_Parses()
    {
        var table = new DataTable();
        table.Columns.Add("col", typeof(string));
        table.Rows.Add("99");
        using var reader = table.CreateDataReader();
        reader.Read();
        Assert.Equal(99, PlanRowReader.SafeReadInt32(reader, 0));
    }

    [Fact]
    public void SafeReadInt32_DBNull_ReturnsZero()
    {
        var table = new DataTable();
        table.Columns.Add("col", typeof(int));
        table.Rows.Add(DBNull.Value);
        using var reader = table.CreateDataReader();
        reader.Read();
        Assert.Equal(0, PlanRowReader.SafeReadInt32(reader, 0));
    }

    [Fact]
    public void SafeReadInt32_DecimalColumn_ConvertsToInt()
    {
        var table = new DataTable();
        table.Columns.Add("col", typeof(decimal));
        table.Rows.Add(42m);
        using var reader = table.CreateDataReader();
        reader.Read();
        Assert.Equal(42, PlanRowReader.SafeReadInt32(reader, 0));
    }

    [Fact]
    public void SafeReadDecimal_DecimalColumn_ReturnsValue()
    {
        var table = new DataTable();
        table.Columns.Add("col", typeof(decimal));
        table.Rows.Add(3.5m);
        using var reader = table.CreateDataReader();
        reader.Read();
        Assert.Equal(3.5m, PlanRowReader.SafeReadDecimal(reader, 0));
    }

    [Fact]
    public void SafeReadDecimal_IntColumn_ReturnsValue()
    {
        var table = new DataTable();
        table.Columns.Add("col", typeof(int));
        table.Rows.Add(10);
        using var reader = table.CreateDataReader();
        reader.Read();
        Assert.Equal(10m, PlanRowReader.SafeReadDecimal(reader, 0));
    }

    [Fact]
    public void SafeReadDecimal_StringNumeric_Parses()
    {
        var table = new DataTable();
        table.Columns.Add("col", typeof(string));
        table.Rows.Add("2.5");
        using var reader = table.CreateDataReader();
        reader.Read();
        Assert.Equal(2.5m, PlanRowReader.SafeReadDecimal(reader, 0));
    }

    [Fact]
    public void SafeReadDecimal_DBNull_ReturnsZero()
    {
        var table = new DataTable();
        table.Columns.Add("col", typeof(decimal));
        table.Rows.Add(DBNull.Value);
        using var reader = table.CreateDataReader();
        reader.Read();
        Assert.Equal(0m, PlanRowReader.SafeReadDecimal(reader, 0));
    }

    [Fact]
    public void SafeReadBoolean_BoolTrue_ReturnsTrue()
    {
        var table = new DataTable();
        table.Columns.Add("col", typeof(bool));
        table.Rows.Add(true);
        using var reader = table.CreateDataReader();
        reader.Read();
        Assert.True(PlanRowReader.SafeReadBoolean(reader, 0));
    }

    [Fact]
    public void SafeReadBoolean_BoolFalse_ReturnsFalse()
    {
        var table = new DataTable();
        table.Columns.Add("col", typeof(bool));
        table.Rows.Add(false);
        using var reader = table.CreateDataReader();
        reader.Read();
        Assert.False(PlanRowReader.SafeReadBoolean(reader, 0));
    }

    [Fact]
    public void SafeReadBoolean_IntOne_ReturnsTrue()
    {
        var table = new DataTable();
        table.Columns.Add("col", typeof(int));
        table.Rows.Add(1);
        using var reader = table.CreateDataReader();
        reader.Read();
        Assert.True(PlanRowReader.SafeReadBoolean(reader, 0));
    }

    [Fact]
    public void SafeReadBoolean_StringTrue_ReturnsTrue()
    {
        var table = new DataTable();
        table.Columns.Add("col", typeof(string));
        table.Rows.Add("true");
        using var reader = table.CreateDataReader();
        reader.Read();
        Assert.True(PlanRowReader.SafeReadBoolean(reader, 0));
    }

    [Fact]
    public void SafeReadBoolean_DBNull_ReturnsFalse()
    {
        var table = new DataTable();
        table.Columns.Add("col", typeof(bool));
        table.Rows.Add(DBNull.Value);
        using var reader = table.CreateDataReader();
        reader.Read();
        Assert.False(PlanRowReader.SafeReadBoolean(reader, 0));
    }

    [Fact]
    public void SafeReadBoolean_StringT_ReturnsTrue()
    {
        var table = new DataTable();
        table.Columns.Add("col", typeof(string));
        table.Rows.Add("t");
        using var reader = table.CreateDataReader();
        reader.Read();
        Assert.True(PlanRowReader.SafeReadBoolean(reader, 0));
    }

    [Fact]
    public void SafeReadBoolean_StringDa_ReturnsTrue()
    {
        var table = new DataTable();
        table.Columns.Add("col", typeof(string));
        table.Rows.Add("да");
        using var reader = table.CreateDataReader();
        reader.Read();
        Assert.True(PlanRowReader.SafeReadBoolean(reader, 0));
    }

    [Fact]
    public void SafeReadDecimal_LongColumn_ConvertsToDecimal()
    {
        var table = new DataTable();
        table.Columns.Add("col", typeof(long));
        table.Rows.Add(100L);
        using var reader = table.CreateDataReader();
        reader.Read();
        Assert.Equal(100m, PlanRowReader.SafeReadDecimal(reader, 0));
    }
}
