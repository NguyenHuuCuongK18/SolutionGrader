using System;
using System.Collections.Generic;
using System.IO;
using OfficeOpenXml;
using SolutionGrader.Legacy.Model;

namespace SolutionGrader.Services
{
    public class TestCaseData
    {
        public List<Input_Client> InputSteps { get; } = new();
        public Dictionary<int, OutputClient> ExpectedClients { get; } = new();
        public Dictionary<int, OutputServer> ExpectedServers { get; } = new();
    }

    public static class TestCaseLoader
    {
        public static TestCaseData Load(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("Test case file path is required.", nameof(filePath));
            }

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Test case file not found: {filePath}", filePath);
            }

            ExcelPackage.License.SetNonCommercialPersonal("AGS FPT");
            var data = new TestCaseData();

            using var package = new ExcelPackage(new FileInfo(filePath));

            LoadInputSheet(package.Workbook.Worksheets["InputClients"], data);
            LoadClientSheet(package.Workbook.Worksheets["OutputClients"], data);
            LoadServerSheet(package.Workbook.Worksheets["OutputServers"], data);

            return data;
        }

        private static void LoadInputSheet(ExcelWorksheet? sheet, TestCaseData data)
        {
            if (sheet == null)
            {
                GraderLogger.Warning("Input_Client sheet not found in test case file.");
                return;
            }

            int rows = sheet.Dimension?.Rows ?? 0;
            for (int row = 2; row <= rows; row++)
            {
                if (!TryParseStage(sheet.Cells[row, 1].Text, out int stage))
                {
                    continue;
                }

                var action = sheet.Cells[row, 4].Text?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(action))
                {
                    continue;
                }

                var input = sheet.Cells[row, 2].Text?.Trim() ?? string.Empty;
                var dataType = sheet.Cells[row, 3].Text?.Trim() ?? string.Empty;

                data.InputSteps.Add(new Input_Client
                {
                    Stage = stage,
                    Action = action,
                    Input = input,
                    DataType = dataType
                });
            }

            data.InputSteps.Sort((a, b) => a.Stage.CompareTo(b.Stage));
        }

        private static void LoadClientSheet(ExcelWorksheet? sheet, TestCaseData data)
        {
            if (sheet == null)
            {
                GraderLogger.Warning("Output_Client sheet not found in test case file.");
                return;
            }

            int rows = sheet.Dimension?.Rows ?? 0;
            for (int row = 2; row <= rows; row++)
            {
                if (!TryParseStage(sheet.Cells[row, 1].Text, out int stage))
                {
                    continue;
                }

                var client = new OutputClient
                {
                    Stage = stage,
                    Method = sheet.Cells[row, 2].Text?.Trim() ?? string.Empty,
                    DataResponse = sheet.Cells[row, 3].Text ?? string.Empty,
                    StatusCode = sheet.Cells[row, 4].Text,
                    Output = sheet.Cells[row, 5].Text ?? string.Empty,
                    DataTypeMiddleWare = sheet.Cells[row, 6].Text?.Trim() ?? string.Empty,
                    ByteSize = sheet.Cells[row, 7].Text
                };

                data.ExpectedClients[stage] = client;
            }
        }

        private static void LoadServerSheet(ExcelWorksheet? sheet, TestCaseData data)
        {
            if (sheet == null)
            {
                GraderLogger.Warning("Output_Server sheet not found in test case file.");
                return;
            }

            int rows = sheet.Dimension?.Rows ?? 0;
            for (int row = 2; row <= rows; row++)
            {
                if (!TryParseStage(sheet.Cells[row, 1].Text, out int stage))
                {
                    continue;
                }

                var server = new OutputServer
                {
                    Stage = stage,
                    Method = sheet.Cells[row, 2].Text?.Trim() ?? string.Empty,
                    DataRequest = sheet.Cells[row, 3].Text ?? string.Empty,
                    Output = sheet.Cells[row, 4].Text ?? string.Empty,
                    DataTypeMiddleware = sheet.Cells[row, 5].Text?.Trim() ?? string.Empty,
                    ByteSize = sheet.Cells[row, 6].Text
                };

                data.ExpectedServers[stage] = server;
            }
        }

        private static bool TryParseStage(string? text, out int stage)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                stage = default;
                return false;
            }

            return int.TryParse(text, out stage);
        }

        private static int TryParseInt(string? text)
        {
            if (int.TryParse(text, out int value))
            {
                return value;
            }

            return 0;
        }
    }
}