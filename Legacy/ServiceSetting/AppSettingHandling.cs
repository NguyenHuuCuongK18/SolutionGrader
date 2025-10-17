using System.IO;

namespace SolutionGrader.Legacy.ServiceSetting
{
    public class AppSettingHandling
    {
        /// <summary>
        /// Ghi đè tất cả file cấu hình (VD: appsettings.json) trong thư mục đích bằng file template.
        /// </summary>
        /// <param name="templatePath">Đường dẫn file template (nguồn copy).</param>
        /// <param name="destinationDir">Thư mục gốc cần quét và ghi đè file.</param>
        /// <param name="targetFileName">Tên file cần tìm để thay thế, ví dụ "appsettings.json".</param>
        /// <param name="replaceOnlyPublish">
        /// Nếu true → chỉ ghi đè file trong thư mục "publish".  
        /// Nếu false → ghi đè tất cả file (bao gồm cả trong publish).
        /// </param>
        public static void ReplaceAppSetting(string templatePath, string destinationDir, string targetFileName, bool replaceOnlyPublish = false)
        {
            if (!File.Exists(templatePath))
            {
                Console.WriteLine($"Template file not found: {templatePath}");
                return;
            }

            if (!Directory.Exists(destinationDir))
            {
                Console.WriteLine($"Destination directory not found: {destinationDir}");
                return;
            }

            // Tìm toàn bộ file cần thay thế
            string[] searchResults = Directory.GetFiles(destinationDir, targetFileName, SearchOption.AllDirectories);

            // Áp dụng lọc theo logic mới
            if (replaceOnlyPublish)
            {
                searchResults = searchResults
                    .Where(path => path.Contains("publish", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            int replaced = 0;
            foreach (string item in searchResults)
            {
                try
                {
                    File.Copy(templatePath, item, true);
                    replaced++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error replacing file '{item}': {ex.Message}");
                }
            }

            Console.WriteLine($"Replaced {replaced} file(s) named '{targetFileName}' in '{destinationDir}'.");
        }
    }
}
