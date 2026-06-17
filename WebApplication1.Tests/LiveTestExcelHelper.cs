using ClosedXML.Excel;

namespace WebApplication1.Tests;

public static class LiveTestExcelHelper
{
    public static byte[] CreateFacultyImportWorkbook(string fullName, string departmentName)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("ППС");
        ws.Cell(1, 1).Value = "ФИО";
        ws.Cell(1, 2).Value = "Департамент";
        ws.Cell(1, 3).Value = "Должность";
        ws.Cell(2, 1).Value = fullName;
        ws.Cell(2, 2).Value = departmentName;
        ws.Cell(2, 3).Value = "Преподаватель";
        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        return stream.ToArray();
    }
}
