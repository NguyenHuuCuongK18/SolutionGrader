using OfficeOpenXml;
using System.IO;

namespace SolutionGrader.Legacy.Service
{
    public class IgnoreListLoader
    {
        public static HashSet<string> IgnoreLoader(string excelPath)
        {
            var ignoreSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(excelPath)) return ignoreSet;

            ExcelPackage.License.SetNonCommercialPersonal("AGS FPT");
            using var package = new ExcelPackage(new FileInfo(excelPath));
            var sheet = package.Workbook.Worksheets[0];
            int rowCount = sheet.Dimension?.Rows ?? 0;

            for (int row = 2; row <= rowCount; row++)
            {
                string? text = sheet.Cells[row, 1].Text?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    ignoreSet.Add(text);
                }
            }
            return ignoreSet;
        }
    }
}
