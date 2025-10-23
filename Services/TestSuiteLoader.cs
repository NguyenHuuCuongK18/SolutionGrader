using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OfficeOpenXml;

namespace SolutionGrader.Services
{
    public sealed class TestSuiteDefinition
    {
        public TestSuiteDefinition(string headerPath, string protocol, IReadOnlyList<TestSuiteCase> cases)
        {
            HeaderPath = headerPath;
            Protocol = protocol;
            Cases = cases;
        }

        public string HeaderPath { get; }
        public string Protocol { get; }
        public IReadOnlyList<TestSuiteCase> Cases { get; }
        public string RootDirectory => Path.GetDirectoryName(HeaderPath)!;
    }

    public sealed class TestSuiteCase
    {
        public TestSuiteCase(string name, double mark, string directoryPath, string detailPath, string? headerPath)
        {
            Name = name;
            Mark = mark;
            DirectoryPath = directoryPath;
            DetailPath = detailPath;
            HeaderPath = headerPath;
        }

        public string Name { get; }
        public double Mark { get; }
        public string DirectoryPath { get; }
        public string DetailPath { get; }
        public string? HeaderPath { get; }
    }

    public static class TestSuiteLoader
    {
        private static readonly string[] HeaderFileCandidates = { "Header.xlsx" };
        private static readonly string[] DetailFilePatterns = { "detail*.xlsx", "*.detail.xlsx" };

        public static TestSuiteDefinition Load(string suitePath)
        {
            if (string.IsNullOrWhiteSpace(suitePath))
            {
                throw new ArgumentException("Suite path is required.", nameof(suitePath));
            }

            var headerPath = ResolveHeaderPath(suitePath);
            if (!File.Exists(headerPath))
            {
                throw new FileNotFoundException($"Unable to locate suite header file at {headerPath}");
            }

            ExcelPackage.License.SetNonCommercialPersonal("AGS FPT");

            using var package = new ExcelPackage(new FileInfo(headerPath));

            var protocol = ReadProtocol(package.Workbook.Worksheets["Config"]);
            var cases = ReadCases(package.Workbook.Worksheets["QuestionMark"], headerPath);

            return new TestSuiteDefinition(headerPath, protocol, cases);
        }

        private static string ResolveHeaderPath(string suitePath)
        {
            if (File.Exists(suitePath))
            {
                return suitePath;
            }

            if (!Directory.Exists(suitePath))
            {
                throw new DirectoryNotFoundException($"Suite directory not found: {suitePath}");
            }

            foreach (var candidate in HeaderFileCandidates)
            {
                var path = Path.Combine(suitePath, candidate);
                if (File.Exists(path))
                {
                    return path;
                }
            }

            var header = Directory.GetFiles(suitePath, "*.xlsx", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(file =>
                    Path.GetFileNameWithoutExtension(file)
                        .Contains("header", StringComparison.OrdinalIgnoreCase));

            if (header == null)
            {
                throw new FileNotFoundException($"Unable to locate header file within suite directory: {suitePath}");
            }

            return header;
        }

        private static IReadOnlyList<TestSuiteCase> ReadCases(ExcelWorksheet? sheet, string headerPath)
        {
            if (sheet == null)
            {
                throw new InvalidDataException("Suite header must contain a 'QuestionMark' worksheet.");
            }

            var root = Path.GetDirectoryName(headerPath) ?? throw new InvalidOperationException("Unable to determine suite directory.");

            var cases = new List<TestSuiteCase>();
            var rows = sheet.Dimension?.Rows ?? 0;

            for (var row = 2; row <= rows; row++)
            {
                var name = sheet.Cells[row, 1].Text?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var markText = sheet.Cells[row, 2].Text?.Trim();
                var mark = 0d;
                if (!string.IsNullOrEmpty(markText))
                {
                    double.TryParse(markText, NumberStyles.Float, CultureInfo.InvariantCulture, out mark);
                }

                var caseDirectory = Path.Combine(root, name);
                if (!Directory.Exists(caseDirectory))
                {
                    throw new DirectoryNotFoundException($"Test case directory not found: {caseDirectory}");
                }

                var detailPath = ResolveDetailPath(caseDirectory);
                var innerHeader = ResolveInnerHeaderPath(caseDirectory);

                cases.Add(new TestSuiteCase(name, mark, caseDirectory, detailPath, innerHeader));
            }

            if (cases.Count == 0)
            {
                throw new InvalidDataException("No test cases were defined in the suite header.");
            }

            return cases;
        }

        private static string ResolveDetailPath(string directory)
        {
            foreach (var pattern in DetailFilePatterns)
            {
                var match = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
                if (match != null)
                {
                    return match;
                }
            }

            var detail = Directory.GetFiles(directory, "*.xlsx", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(file =>
                    Path.GetFileNameWithoutExtension(file)
                        .Contains("detail", StringComparison.OrdinalIgnoreCase));

            if (detail == null)
            {
                throw new FileNotFoundException($"Unable to locate detail file for test case directory: {directory}");
            }

            return detail;
        }

        private static string? ResolveInnerHeaderPath(string directory)
        {
            var header = Directory.GetFiles(directory, "*.xlsx", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(file =>
                    Path.GetFileNameWithoutExtension(file)
                        .Contains("header", StringComparison.OrdinalIgnoreCase));

            return header;
        }

        private static string ReadProtocol(ExcelWorksheet? sheet)
        {
            if (sheet == null)
            {
                return "HTTP";
            }

            var rows = sheet.Dimension?.Rows ?? 0;
            for (var row = 2; row <= rows; row++)
            {
                var key = sheet.Cells[row, 1].Text?.Trim();
                if (!string.Equals(key, "Type", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = sheet.Cells[row, 2].Text?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Equals("TCP", StringComparison.OrdinalIgnoreCase) ? "TCP" : "HTTP";
                }
            }

            return "HTTP";
        }
    }
}