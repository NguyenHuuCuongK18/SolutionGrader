using OfficeOpenXml;
using OfficeOpenXml.Style;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SolutionGrader.Services;

namespace SolutionGrader.Legacy.FileHelper
{
    public class ExcelExporter
    {
        public ExcelExporter()
        {
            ExcelPackage.License.SetNonCommercialPersonal("FPT AGS");
        }

        public void ExportToExcelParams(string filePath, params (string SheetName, ICollection<object> Data)[] sheetsData)
        {
            try
            {
                if (sheetsData == null || sheetsData.Length == 0)
                    throw new ArgumentException("Không có dữ liệu để xuất.");

                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using var package = new ExcelPackage();
                bool anyWorksheetAdded = false;

                foreach (var (sheetName, data) in sheetsData)
                {
                    if (data == null || !data.Any()) continue;

                    var firstItem = data.FirstOrDefault(d => d != null);
                    if (firstItem == null) continue;

                    var ws = package.Workbook.Worksheets.Add(
                        string.IsNullOrWhiteSpace(sheetName) ? "Sheet" + (package.Workbook.Worksheets.Count + 1) : sheetName);
                    anyWorksheetAdded = true;

                    var props = firstItem.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

                    // Header
                    for (int i = 0; i < props.Length; i++)
                        ws.Cells[1, i + 1].Value = props[i].Name;

                    using (var header = ws.Cells[1, 1, 1, props.Length])
                        header.Style.Font.Bold = true;

                    // Rows
                    int row = 2;
                    foreach (var item in data)
                    {
                        if (item == null) continue;
                        for (int col = 0; col < props.Length; col++)
                            ws.Cells[row, col + 1].Value = props[col].GetValue(item);
                        row++;
                    }

                    // Column sizing
                    const double MAXW = 60, MINW = 10;
                    for (int i = 1; i <= props.Length; i++)
                    {
                        var col = ws.Column(i);
                        col.AutoFit();
                        if (col.Width > MAXW) col.Width = MAXW;
                        if (col.Width < MINW) col.Width = MINW;
                        col.Style.WrapText = true;
                    }

                    if (ws.Dimension != null)
                        ws.Cells[ws.Dimension.Address].Style.VerticalAlignment = ExcelVerticalAlignment.Top;
                }

                // Nếu không có sheet nào được add, tạo sheet Info để workbook hợp lệ
                if (!anyWorksheetAdded)
                {
                    var info = package.Workbook.Worksheets.Add("Info");
                    info.Cells[1, 1].Value = "No data was available at export time.";
                    info.Cells[2, 1].Value = "A placeholder sheet is added to keep the workbook valid.";
                }

                package.SaveAs(new FileInfo(filePath));
                GraderLogger.Info($"Excel exported: {filePath}");
            }
            catch (Exception ex)
            {
                // KHÔNG bật dialog — chỉ log, để tầng gọi tự quyết định
                GraderLogger.Error($"Export Excel failed for {filePath}", ex);
                throw;
            }
        }
    }
}
