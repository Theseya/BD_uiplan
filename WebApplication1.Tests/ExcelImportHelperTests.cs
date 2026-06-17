using ClosedXML.Excel;
using WebApplication1;
using Xunit;

namespace WebApplication1.Tests;

public class ExcelImportHelperTests
{
    [Fact]
    public void ParseSheet_MapsHeadersAndReadsRows()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        ws.Cell(1, 1).Value = "ФИО";
        ws.Cell(1, 2).Value = "Департамент";
        ws.Cell(2, 1).Value = "Иванов И.И.";
        ws.Cell(2, 2).Value = "Департамент маркетинга";
        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        stream.Position = 0;

        var map = new Dictionary<string, string[]>
        {
            ["full_name"] = new[] { "ФИО" },
            ["department"] = new[] { "Департамент" }
        };
        var rows = ExcelImportHelper.ParseSheet(stream, map);

        Assert.Single(rows);
        Assert.Equal("Иванов И.И.", ExcelImportHelper.Get(rows[0], "full_name"));
        Assert.Equal("Департамент маркетинга", ExcelImportHelper.Get(rows[0], "department"));
    }
}
