using SolutionGrader.Legacy.FileHelper;
using SolutionGrader.Legacy.MiddlewareHandling;
using SolutionGrader.Legacy.Model;
using SolutionGrader.Legacy.Service;
using SolutionGrader.Legacy.ServiceExcute;
using SolutionGrader.Legacy.ServiceSetting;
using SolutionGrader.Models;
using SolutionGrader.Recorder;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
// NOTE: Always qualify Application with System.Windows.Application
using System.Windows;

namespace SolutionGrader.Services
{
    public class GradingResult
    {
        public GradingResult(bool success, string message, string? exportPath, bool cancelled = false)
        {
            Success = success;
            Message = message;
            ExportPath = exportPath;
            Cancelled = cancelled;
        }

        public bool Success { get; }
        public string Message { get; }
        public string? ExportPath { get; }
        public bool Cancelled { get; }
    }

    internal sealed class StageEvaluation
    {
        public StageEvaluation(bool passed, IReadOnlyList<string> issues)
        {
            Passed = passed;
            Issues = issues;
        }

        public bool Passed { get; }
        public IReadOnlyList<string> Issues { get; }
        public string DisplayText => Passed ? "PASS" : string.Join(Environment.NewLine, Issues);
    }

    internal sealed class TestCaseSummary
    {
        public TestCaseSummary(TestSuiteCase definition, bool passed, IReadOnlyList<string> reasons, string exportPath)
        {
            Definition = definition;
            Passed = passed;
            Reasons = reasons;
            ExportPath = exportPath;
        }

        public TestSuiteCase Definition { get; }
        public bool Passed { get; }
        public IReadOnlyList<string> Reasons { get; }
        public string ExportPath { get; }
    }

    public class GradingSession
    {
        private readonly GraderConfig _config;
        private readonly ObservableCollection<TestStepResult> _testSteps;
        private readonly ObservableCollection<OutputClient> _studentClient;
        private readonly ObservableCollection<OutputServer> _studentServer;
        private readonly GraderRecorderContext _recorderContext;
        private readonly ExecutableManager _manager = ExecutableManager.Instance;
        private readonly MiddlewareStart _middleware = MiddlewareStart.Instance;
        private TestCaseData? _testCase;
        private CancellationToken _token;
        private List<string> _currentCaseIssues = new();

        private string? _resultsRoot; // keep results root beyond local scope

        private static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(10);

        public GradingSession(
            GraderConfig config,
            ObservableCollection<TestStepResult> testSteps,
            ObservableCollection<OutputClient> studentClient,
            ObservableCollection<OutputServer> studentServer,
            GraderRecorderContext recorderContext)
        {
            _config = config;
            _testSteps = testSteps;
            _studentClient = studentClient;
            _studentServer = studentServer;
            _recorderContext = recorderContext;
        }

        public async Task<GradingResult> RunAsync(CancellationToken token)
        {
            _token = token;
            try
            {
                ValidateConfig();
                var suite = TestSuiteLoader.Load(_config.SuitePath);
                _resultsRoot = PrepareResultsRoot(suite);

                await RunOnUiThreadAsync(() =>
                {
                    _testSteps.Clear();
                    _studentClient.Clear();
                    _studentServer.Clear();
                });

                _recorderContext.Reset();
                _recorderContext.StageAdded += OnStageAdded;

                SubscribeManagerEvents();
                _middleware.Recorder = _recorderContext;
                ReplaceAppSettings();

                var summaries = new List<TestCaseSummary>();
                foreach (var testCase in suite.Cases)
                {
                    _token.ThrowIfCancellationRequested();

                    // 🔁 Init process objects for EACH test case (they were nulled after StopAll)
                    _manager.Init(_config.ClientPath, _config.ServerPath);

                    var summary = await RunTestCaseAsync(testCase, _resultsRoot);
                    summaries.Add(summary);
                }

                await WriteSummaryAsync(summaries, _resultsRoot);

                return new GradingResult(true, "Grading completed successfully.", _resultsRoot);
            }
            catch (OperationCanceledException)
            {
                GraderLogger.Warning("Grading session cancelled by user.");
                await StopAllAsync();
                return new GradingResult(false, "Grading cancelled.", null, cancelled: true);
            }
            catch (Exception ex)
            {
                GraderLogger.Error("Grading session failed.", ex);

                // Attempt a best-effort export of whatever is currently in memory
                try { await ExportResultsAsync(); } catch { /* ignore */ }

                await StopAllAsync();
                return new GradingResult(false, ex.Message, _resultsRoot);
            }
            finally
            {
                _recorderContext.StageAdded -= OnStageAdded;
                UnsubscribeManagerEvents();
                if (_middleware.Recorder == _recorderContext)
                {
                    _middleware.Recorder = null;
                }
            }
        }

        private async Task<TestCaseSummary> RunTestCaseAsync(TestSuiteCase definition, string resultsRoot)
        {
            GraderLogger.Info($"Starting test case '{definition.Name}'.");

            await RunOnUiThreadAsync(() =>
            {
                _testSteps.Clear();
                _studentClient.Clear();
                _studentServer.Clear();
            });

            _recorderContext.Reset();
            _currentCaseIssues = new List<string>();

            try
            {
                _testCase = TestCaseLoader.Load(definition.DetailPath);
                GraderLogger.Info($"Loaded test case detail with {_testCase.InputSteps.Count} steps.");

                await ResetDatabaseIfNeededAsync();

                if (_testCase.InputSteps.Count == 0)
                    throw new InvalidOperationException("Test case does not contain any steps.");

                var orderedSteps = _testCase.InputSteps.OrderBy(s => s.Stage).ToList();

                var firstStep = orderedSteps.First();
                await RunOnUiThreadAsync(() =>
                {
                    _recorderContext.AddActionStage(firstStep.Action, firstStep.Input, firstStep.DataType, firstStep.Stage);
                });

                await EvaluateAndTrackStepAsync(firstStep, isInitialStep: true);

                foreach (var step in orderedSteps.Skip(1))
                {
                    _token.ThrowIfCancellationRequested();

                    await RunOnUiThreadAsync(() =>
                    {
                        _recorderContext.AddActionStage(step.Action, step.Input, step.DataType, step.Stage);
                    });

                    await ExecuteStepActionAsync(step);
                    await EvaluateAndTrackStepAsync(step, isInitialStep: false);
                }

                // Export once per case into resultsRoot/TestCase_xxx/graderesult.xlsx
                var exportPath = await ExportResultsAsync(definition.Name, resultsRoot);
                await StopAllAsync();

                var passed = !_currentCaseIssues.Any() &&
                             _testSteps.All(s => string.Equals(s.Result, "PASS", StringComparison.OrdinalIgnoreCase));
                var reasons = passed ? Array.Empty<string>() : _currentCaseIssues.Distinct().ToArray();

                return new TestCaseSummary(definition, passed, reasons, exportPath);
            }
            finally
            {
                await StopAllAsync();
            }
        }

        private async Task EvaluateAndTrackStepAsync(Input_Client step, bool isInitialStep)
        {
            _token.ThrowIfCancellationRequested();

            if (isInitialStep)
            {
                await StartProcessesAsync();
            }

            await WaitForStageCompletionAsync(step.Stage);

            await RunOnUiThreadAsync(() =>
            {
                var existing = _testSteps.FirstOrDefault(s => s.Step == step.Stage);
                if (existing == null)
                {
                    existing = CreateTestStepResult(step);
                    _testSteps.Add(existing);
                }

                var evaluation = EvaluateStage(step.Stage);
                existing.Result = evaluation.DisplayText;

                if (!evaluation.Passed)
                {
                    _currentCaseIssues.AddRange(evaluation.Issues);
                }
            });
        }

        private TestStepResult CreateTestStepResult(Input_Client step)
        {
            var builder = new StringBuilder();
            builder.Append(step.Action);
            if (!string.IsNullOrWhiteSpace(step.Input)) builder.Append($" | Input: {step.Input}");
            if (!string.IsNullOrWhiteSpace(step.DataType)) builder.Append($" | Type: {step.DataType}");

            return new TestStepResult
            {
                Step = step.Stage,
                Input = builder.ToString(),
                Result = "Pending"
            };
        }

        private void OnStageAdded(Input_Client stage)
        {
            void AddStage()
            {
                if (_testSteps.All(s => s.Step != stage.Stage))
                {
                    _testSteps.Add(CreateTestStepResult(stage));
                }
            }

            if (System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                AddStage();
            }
            else
            {
                System.Windows.Application.Current.Dispatcher.Invoke(AddStage);
            }
        }

        private async Task ExecuteStepActionAsync(Input_Client step)
        {
            try
            {
                var action = (step.Action ?? "").Trim().ToLowerInvariant();
                bool useHttp = !string.Equals(_config.Protocol, "TCP", StringComparison.OrdinalIgnoreCase);

                switch (action)
                {
                    case "client input":
                        GraderLogger.Info($"Sending client input for stage {step.Stage}: {step.Input}");
                        _manager.SendClientInput(step.Input);
                        break;

                    case "clientclose":
                        GraderLogger.Info("Stopping client process as requested by test step.");
                        await _manager.StopClientAsync();
                        // keep middleware behavior consistent with generator
                        await _middleware.StopAsync();
                        break;

                    case "serverclose":
                        GraderLogger.Info("Stopping server process as requested by test step.");
                        await _manager.StopServerAsync();
                        // keep middleware behavior consistent with generator
                        await _middleware.StopAsync();
                        break;

                    case "clientstart":
                        // server may already be running or not; safe to start middleware redundantly
                        if (!_manager.IsServerRunning)
                        {
                            GraderLogger.Info("Clientstart requested; ensuring server is running first.");
                            _manager.StartServer();
                        }
                        await _middleware.StartAsync(useHttp);   // no-op if already running
                        _manager.StartClient();
                        break;

                    case "serverstart":
                        _manager.StartServer();
                        await _middleware.StartAsync(useHttp);   // start (or keep) proxy with correct mode
                        break;

                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                // Do NOT crash the session; record and continue
                var msg = $"Failed to execute action '{step.Action}' for stage {step.Stage}: {ex.Message}";
                GraderLogger.Error(msg, ex);
                _currentCaseIssues.Add(msg);
                // no rethrow
            }
        }

        // Trong GradingSession.cs
        private async Task StartProcessesAsync()
        {
            try
            {
                GraderLogger.Info("Starting server process.");
                _manager.StartServer();

                // Retry đợi server sẵn sàng ~2s
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(2))
                {
                    if (_manager.IsServerRunning) break;
                    await Task.Delay(100, _token);
                }

                if (!_manager.IsServerRunning)
                {
                    GraderLogger.Warning("Server not fully initialized after wait. Will continue and rely on proxy retries.");
                }

                bool useHttp = !string.Equals(_config.Protocol, "TCP", StringComparison.OrdinalIgnoreCase);
                GraderLogger.Info($"Starting middleware proxy. HTTP Mode: {useHttp}");
                await _middleware.StartAsync(useHttp);

                GraderLogger.Info("Starting client process.");
                _manager.StartClient();
            }
            catch (Exception ex)
            {
                GraderLogger.Error("Failed to start external processes.", ex);
                throw; // KHÔNG bật dialog ở đây
            }
        }

        private async Task WaitForStageCompletionAsync(int stage)
        {
            var expectedClient = _testCase!.ExpectedClients.TryGetValue(stage, out var client) ? client : null;
            var expectedServer = _testCase!.ExpectedServers.TryGetValue(stage, out var server) ? server : null;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (stopwatch.Elapsed < StageTimeout)
            {
                _token.ThrowIfCancellationRequested();

                bool clientReady = !HasMeaningfulClientExpectation(expectedClient) || await HasClientResultAsync(stage);
                bool serverReady = !HasMeaningfulServerExpectation(expectedServer) || await HasServerResultAsync(stage);

                if (clientReady && serverReady) return;

                await Task.Delay(200, _token);
            }

            GraderLogger.Warning($"Timeout waiting for stage {stage} results.");
        }

        private async Task<bool> HasClientResultAsync(int stage)
        {
            var result = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                _recorderContext.OutputClients.LastOrDefault(c => c.Stage == stage));

            return result != null && HasMeaningfulClientExpectation(result);
        }

        private async Task<bool> HasServerResultAsync(int stage)
        {
            var result = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                _recorderContext.OutputServers.LastOrDefault(c => c.Stage == stage));

            return result != null && HasMeaningfulServerExpectation(result);
        }

        private StageEvaluation EvaluateStage(int stage)
        {
            var issues = new List<string>();

            var expectedClient = _testCase!.ExpectedClients.TryGetValue(stage, out var client) ? client : null;
            var expectedServer = _testCase!.ExpectedServers.TryGetValue(stage, out var server) ? server : null;

            var actualClient = _recorderContext.OutputClients.LastOrDefault(c => c.Stage == stage);
            var actualServer = _recorderContext.OutputServers.LastOrDefault(c => c.Stage == stage);

            EvaluateClient(stage, expectedClient, actualClient, issues);
            EvaluateServer(stage, expectedServer, actualServer, issues);

            if (issues.Count == 0) return new StageEvaluation(true, Array.Empty<string>());

            foreach (var issue in issues) GraderLogger.Warning(issue);
            return new StageEvaluation(false, issues);
        }

        private void EvaluateClient(int stage, OutputClient? expected, OutputClient? actual, List<string> issues)
        {
            if (!HasMeaningfulClientExpectation(expected)) return;

            if (actual == null)
            {
                issues.Add($"Stage {stage}: Missing client output.");
                return;
            }

            CompareString("Client Method", expected!.Method, actual.Method, issues, stage);
            CompareStatusCode(expected.StatusCode, actual.StatusCode, issues, stage);
            CompareByteSize("Client", expected.ByteSize, actual.ByteSize, issues, stage);
            CompareString("Client DataType", expected.DataTypeMiddleWare, actual.DataTypeMiddleWare, issues, stage);

            if (!ComparePayload(expected.DataResponse, actual.DataResponse, expected.DataTypeMiddleWare))
                issues.Add($"Stage {stage}: Client DataResponse mismatch.");

            if (!CompareText(expected.Output, actual.Output))
                issues.Add($"Stage {stage}: Client console output mismatch.");
        }

        private void EvaluateServer(int stage, OutputServer? expected, OutputServer? actual, List<string> issues)
        {
            if (!HasMeaningfulServerExpectation(expected)) return;

            if (actual == null)
            {
                issues.Add($"Stage {stage}: Missing server output.");
                return;
            }

            CompareString("Server Method", expected!.Method, actual.Method, issues, stage);
            CompareByteSize("Server", expected.ByteSize, actual.ByteSize, issues, stage);
            CompareString("Server DataType", expected.DataTypeMiddleware, actual.DataTypeMiddleware, issues, stage);

            if (!CompareText(expected.DataRequest, actual.DataRequest))
                issues.Add($"Stage {stage}: Server request payload mismatch.");

            if (!CompareText(expected.Output, actual.Output))
                issues.Add($"Stage {stage}: Server console output mismatch.");
        }

        private void CompareString(string field, string expected, string actual, List<string> issues, int stage)
        {
            if (string.Equals(expected?.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase)) return;
            issues.Add($"Stage {stage}: {field} expected '{expected}', actual '{actual}'.");
        }

        private void CompareStatusCode(string expected, string actual, List<string> issues, int stage)
        {
            if (string.Equals(expected?.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase)) return;
            if (expected != actual)
                issues.Add($"Stage {stage}: StatusCode expected {expected}, actual {actual}.");
        }

        private void CompareByteSize(string scope, string expected, string actual, List<string> issues, int stage)
        {
            if (string.Equals(expected?.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase)) return;
            if (expected != actual)
                issues.Add($"Stage {stage}: {scope} byte size expected {expected}, actual {actual}.");
        }

        private bool ComparePayload(string expected, string actual, string dataType)
        {
            if (string.IsNullOrWhiteSpace(expected) && string.IsNullOrWhiteSpace(actual)) return true;

            if (string.Equals(dataType, "JSON", StringComparison.OrdinalIgnoreCase))
                return DataCompare.CompareJson(expected, actual);

            if (string.Equals(dataType, "XML", StringComparison.OrdinalIgnoreCase))
                return DataCompare.CompareXml(expected, actual);

            return CompareText(expected, actual);
        }

        private bool CompareText(string expected, string actual)
        {
            return string.Equals(
                DataCompare.NormalizeData(expected),
                DataCompare.NormalizeData(actual),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string value) =>
            (value ?? string.Empty).Replace("\r", string.Empty).Trim();

        private bool HasMeaningfulClientExpectation(OutputClient? client)
        {
            if (client == null) return false;

            return !(string.IsNullOrWhiteSpace(client.Method)
                     && string.IsNullOrWhiteSpace(client.DataResponse)
                     && string.IsNullOrWhiteSpace(client.Output)
                     && string.IsNullOrWhiteSpace(client.DataTypeMiddleWare)
                     && client.StatusCode == null
                     && client.ByteSize == null);
        }

        private bool HasMeaningfulServerExpectation(OutputServer? server)
        {
            if (server == null) return false;

            return !(string.IsNullOrWhiteSpace(server.Method)
                     && string.IsNullOrWhiteSpace(server.DataRequest)
                     && string.IsNullOrWhiteSpace(server.Output)
                     && string.IsNullOrWhiteSpace(server.DataTypeMiddleware)
                     && server.ByteSize == null);
        }

        // Export results for a specific case into resultsRoot/TestCase/graderesult.xlsx
        private async Task<string> ExportResultsAsync(string testCaseName, string resultsRoot)
        {
            var caseDirectory = Path.Combine(resultsRoot, testCaseName);
            Directory.CreateDirectory(caseDirectory);

            var exportPath = Path.Combine(caseDirectory, "graderesult.xlsx");

            await RunOnUiThreadAsync(() =>
            {
                var exporter = new ExcelExporter();
                var testStepObjects = _testSteps.Cast<object>().ToList();
                var clientObjects = _recorderContext.OutputClients.Cast<object>().ToList();
                var serverObjects = _recorderContext.OutputServers.Cast<object>().ToList();

                exporter.ExportToExcelParams(exportPath,
                    ("TestSteps", testStepObjects),
                    ("StudentOutputClient", clientObjects),
                    ("StudentOutputServer", serverObjects));
            });

            GraderLogger.Info($"Exported grading results for '{testCaseName}' to {exportPath}");
            return exportPath;
        }

        // Best-effort no-arg export used in error paths
        private async Task<string> ExportResultsAsync()
        {
            // Ưu tiên _resultsRoot (nếu đã có), rồi đến cấu hình người dùng,
            // cuối cùng rơi về Documents để tránh bin\\Debug.
            string preferredBase = _resultsRoot
                ?? (!string.IsNullOrWhiteSpace(_config.ResultsDirectory)
                    ? _config.ResultsDirectory
                    : Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "SolutionGrader",
                        "GradeResults"));

            Directory.CreateDirectory(preferredBase);

            var root = Path.Combine(preferredBase, $"GradeResults_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(root);

            return await ExportResultsAsync("FailedCase", root);
        }

        private async Task ResetDatabaseIfNeededAsync()
        {
            try
            {
                var connectionString = TryGetConnectionString(_config.ServerAppSettingsPath, "MyCnn");
                if (string.IsNullOrWhiteSpace(connectionString)) return;

                if (string.IsNullOrWhiteSpace(_config.DatabaseScriptPath))
                {
                    GraderLogger.Warning("Detected MyCnn connection string but no database script was provided. Skipping database reset.");
                    return;
                }

                var resetService = new DatabaseResetService(connectionString, _config.DatabaseScriptPath);
                await resetService.ResetAsync(_token).ConfigureAwait(false);
                GraderLogger.Info("Database reset completed successfully.");
            }
            catch (Exception ex)
            {
                GraderLogger.Error("Failed to reset database before running test case.", ex);
                throw;
            }
        }

        private static string? TryGetConnectionString(string path, string key)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

            try
            {
                using var stream = File.OpenRead(path);
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

                if (!document.RootElement.TryGetProperty("ConnectionStrings", out var connectionStrings)) return null;
                if (!connectionStrings.TryGetProperty(key, out var value)) return null;

                return value.GetString();
            }
            catch (JsonException ex)
            {
                GraderLogger.Warning($"Unable to parse JSON configuration file '{path}': {ex.Message}");
                return null;
            }
            catch (IOException ex)
            {
                GraderLogger.Warning($"Unable to read configuration file '{path}': {ex.Message}");
                return null;
            }
        }

        private string PrepareResultsRoot(TestSuiteDefinition suite)
        {
            var baseDirectory = !string.IsNullOrWhiteSpace(_config.ResultsDirectory)
                ? _config.ResultsDirectory
                : Path.Combine(suite.RootDirectory, "GradeResults");

            Directory.CreateDirectory(baseDirectory);

            var folderName = $"GradeResults_{DateTime.Now:yyyyMMdd_HHmmss}";
            var resultRoot = Path.Combine(baseDirectory, folderName);
            Directory.CreateDirectory(resultRoot);

            return resultRoot;
        }

        private async Task WriteSummaryAsync(IReadOnlyList<TestCaseSummary> summaries, string resultsRoot)
        {
            var summaryPath = Path.Combine(resultsRoot, "summary.txt");
            var lines = new List<string>
            {
                "Test Case Summary",
                new string('=', 18),
                string.Empty
            };

            foreach (var summary in summaries)
            {
                var status = summary.Passed ? "PASS" : "FAIL";
                lines.Add($"{summary.Definition.Name} - {status} (Mark: {summary.Definition.Mark})");
                lines.Add($"Result File: {summary.ExportPath}");

                if (!summary.Passed && summary.Reasons.Count > 0)
                {
                    foreach (var reason in summary.Reasons)
                    {
                        lines.Add($"  - {reason}");
                    }
                }

                lines.Add(string.Empty);
            }

            await File.WriteAllLinesAsync(summaryPath, lines, _token);
            GraderLogger.Info($"Summary written to {summaryPath}");
        }

        private void ReplaceAppSettings()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_config.ServerAppSettingsPath))
                {
                    var destServer = Path.GetDirectoryName(_config.ServerPath);
                    if (!string.IsNullOrEmpty(destServer))
                    {
                        AppSettingHandling.ReplaceAppSetting(_config.ServerAppSettingsPath, destServer, "appsettings.json", false);
                    }
                }

                if (!string.IsNullOrWhiteSpace(_config.ClientAppSettingsPath))
                {
                    var destClient = Path.GetDirectoryName(_config.ClientPath);
                    if (!string.IsNullOrEmpty(destClient))
                    {
                        AppSettingHandling.ReplaceAppSetting(_config.ClientAppSettingsPath, destClient, "appsettings.json", false);
                    }
                }
            }
            catch (Exception ex)
            {
                GraderLogger.Error("Failed to replace appsettings files.", ex);
                throw;
            }
        }

        private void ValidateConfig()
        {
            var errors = new List<string>();

            if (!File.Exists(_config.ClientPath))
                errors.Add($"Client executable not found: {_config.ClientPath}");

            if (!File.Exists(_config.ServerPath))
                errors.Add($"Server executable not found: {_config.ServerPath}");

            if (!File.Exists(_config.ClientAppSettingsPath))
                errors.Add($"Client appsettings template not found: {_config.ClientAppSettingsPath}");

            if (!File.Exists(_config.ServerAppSettingsPath))
                errors.Add($"Server appsettings template not found: {_config.ServerAppSettingsPath}");

            if (!File.Exists(_config.SuitePath) && !Directory.Exists(_config.SuitePath))
                errors.Add($"Suite path not found: {_config.SuitePath}");

            if (!string.IsNullOrWhiteSpace(_config.DatabaseScriptPath) && !File.Exists(_config.DatabaseScriptPath))
                errors.Add($"Database script file not found: {_config.DatabaseScriptPath}");

            if (!string.IsNullOrWhiteSpace(_config.ResultsDirectory) && !Directory.Exists(_config.ResultsDirectory))
            {
                try { Directory.CreateDirectory(_config.ResultsDirectory); }
                catch (Exception ex) { errors.Add($"Unable to create results directory '{_config.ResultsDirectory}': {ex.Message}"); }
            }

            if (errors.Count > 0)
                throw new FileNotFoundException(string.Join(Environment.NewLine, errors));
        }

        private async Task StopAllAsync()
        {
            try { await _manager.StopAllAsync(); }
            catch (Exception ex) { GraderLogger.Error("Error while stopping executables.", ex); }

            try { await _middleware.StopAsync(); }
            catch (Exception ex) { GraderLogger.Error("Error while stopping middleware.", ex); }
        }

        private void SubscribeManagerEvents()
        {
            _manager.ClientOutputReceived += OnClientOutputReceived;
            _manager.ServerOutputReceived += OnServerOutputReceived;
        }

        private void UnsubscribeManagerEvents()
        {
            _manager.ClientOutputReceived -= OnClientOutputReceived;
            _manager.ServerOutputReceived -= OnServerOutputReceived;
        }

        private void OnClientOutputReceived(string data)
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _recorderContext.AppendClientOutput(data);
            });
        }

        private void OnServerOutputReceived(string data)
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _recorderContext.AppendServerOutput(data);
            });
        }

        private Task RunOnUiThreadAsync(Action action)
        {
            return System.Windows.Application.Current.Dispatcher.InvokeAsync(action).Task;
        }
    }
}
