using OfficeOpenXml;
using OfficeOpenXml.Style;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;

namespace SolutionGrader.Legacy.FileHelper
{
    public class ExcelExporter
    {
        public ExcelExporter()
        {
            ExcelPackage.License.SetNonCommercialPersonal("AGS FPT");
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

                ExcelPackage.License.SetNonCommercialPersonal("AGS FPT");

                using var package = new ExcelPackage();

                foreach (var (sheetName, data) in sheetsData)
                {
                    if (data == null || !data.Any()) continue;

                    var firstItem = data.FirstOrDefault(d => d != null);
                    if (firstItem == null) continue;

                    var worksheet = package.Workbook.Worksheets.Add(sheetName);

                    var properties = firstItem.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

                    for (int i = 0; i < properties.Length; i++)
                    {
                        worksheet.Cells[1, i + 1].Value = properties[i].Name;
                    }

                    using (var headerRange = worksheet.Cells[1, 1, 1, properties.Length])
                    {
                        headerRange.Style.Font.Bold = true;
                    }

                    if (data.Any())
                    {
                        int currentRow = 2;
                        foreach (var item in data)
                        {
                            if (item == null) continue;
                            for (int i = 0; i < properties.Length; i++)
                            {
                                var value = properties[i].GetValue(item);
                                worksheet.Cells[currentRow, i + 1].Value = value;
                            }
                            currentRow++;
                        }
                    }

                    const double MAX_COLUMN_WIDTH = 60;
                    const double MIN_COLUMN_WIDTH = 10;

                    for (int i = 1; i <= properties.Length; i++)
                    {
                        var column = worksheet.Column(i);
                        var propertyName = properties[i - 1].Name;
                        column.Style.WrapText = true;
                        if ((propertyName.Equals("DataResponse", StringComparison.OrdinalIgnoreCase))
                            || (propertyName.Equals("Output", StringComparison.OrdinalIgnoreCase)))
                        {
                            column.Style.WrapText = true;
                            column.Width = MAX_COLUMN_WIDTH;
                        }
                        else if ((propertyName.Equals("DataTypeMiddleWare", StringComparison.OrdinalIgnoreCase)) ||
                                 (propertyName.Equals("DataRequest", StringComparison.OrdinalIgnoreCase)))
                        {
                            column.Style.WrapText = true;
                            column.Width = MIN_COLUMN_WIDTH * 2;
                        }
                        else
                        {
                            column.AutoFit();
                        }

                        if (column.Width > MAX_COLUMN_WIDTH) column.Width = MAX_COLUMN_WIDTH;
                        if (column.Width < MIN_COLUMN_WIDTH) column.Width = MIN_COLUMN_WIDTH;
                    }

                    if (worksheet.Dimension != null)
                    {
                        worksheet.Cells[worksheet.Dimension.Address].Style.VerticalAlignment = ExcelVerticalAlignment.Top;
                    }
                }

                package.SaveAs(new FileInfo(filePath));

                MessageBox.Show(
                    $"Xuất file Excel thành công!\nĐường dẫn: {filePath}",
                    "Thành công",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }
            catch (Exception ex)
            {
                try
                {
                    string logPath = Path.Combine(Path.GetDirectoryName(filePath) ?? AppDomain.CurrentDomain.BaseDirectory, "ExportLog.txt");
                    File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Lỗi khi export Excel:\n{ex}\n\n");
                    MessageBox.Show($"Xuất Excel thất bại!\nChi tiết lỗi đã được ghi tại:\n{logPath}", "Lỗi Xuất File", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch
                {
                    MessageBox.Show($"Xuất Excel thất bại: {ex.Message}", "Lỗi Xuất File", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}