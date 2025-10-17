using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SolutionGrader.Models;
using SolutionGrader.Recorder;
using SolutionGrader.Legacy.FileHelper;
using SolutionGrader.Legacy.MiddlewareHandling;
using SolutionGrader.Legacy.Model;
using SolutionGrader.Legacy.Service;
using SolutionGrader.Legacy.ServiceExcute;
using SolutionGrader.Legacy.ServiceSetting;

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
                _testCase = TestCaseLoader.Load(_config.TestCaseFilePath);
                GraderLogger.Info($"Loaded test case with {_testCase.InputSteps.Count} steps.");

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

                _manager.Init(_config.ClientPath, _config.ServerPath);

                if (_testCase.InputSteps.Count == 0)
                {
                    throw new InvalidOperationException("Test case does not contain any steps.");
                }

                var orderedSteps = _testCase.InputSteps.OrderBy(s => s.Stage).ToList();

                // Stage 1: Connect (assumes first step)
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

                await ExportResultsAsync();
                await StopAllAsync();

                return new GradingResult(true, "Grading completed successfully.", GetResultExportPath());
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
                await StopAllAsync();
                return new GradingResult(false, ex.Message, null);
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

                existing.Result = EvaluateStage(step.Stage);
            });
        }

        private TestStepResult CreateTestStepResult(Input_Client step)
        {
            var builder = new StringBuilder();
            builder.Append(step.Action);
            if (!string.IsNullOrWhiteSpace(step.Input))
            {
                builder.Append($" | Input: {step.Input}");
            }
            if (!string.IsNullOrWhiteSpace(step.DataType))
            {
                builder.Append($" | Type: {step.DataType}");
            }

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

            if (Application.Current.Dispatcher.CheckAccess())
            {
                AddStage();
            }
            else
            {
                Application.Current.Dispatcher.Invoke(AddStage);
            }
        }

        private async Task ExecuteStepActionAsync(Input_Client step)
        {
            try
            {
                switch (step.Action?.Trim().ToLowerInvariant())
                {
                    case "client input":
                        GraderLogger.Info($"Sending client input for stage {step.Stage}: {step.Input}");
                        _manager.SendClientInput(step.Input);
                        break;
                    case "clientclose":
                        GraderLogger.Info("Stopping client process as requested by test step.");
                        await _manager.StopClientAsync();
                        break;
                    case "serverclose":
                        GraderLogger.Info("Stopping server process as requested by test step.");
                        await _manager.StopServerAsync();
                        break;
                    default:
                        // No direct action required
                        break;
                }
            }
            catch (Exception ex)
            {
                GraderLogger.Error($"Failed to execute action '{step.Action}' for stage {step.Stage}.", ex);
                throw;
            }
        }

        private async Task StartProcessesAsync()
        {
            try
            {
                GraderLogger.Info("Starting server process.");
                _manager.StartServer();

                bool useHttp = !string.Equals(_config.Protocol, "TCP", StringComparison.OrdinalIgnoreCase);
                GraderLogger.Info($"Starting middleware proxy. HTTP Mode: {useHttp}");
                await _middleware.StartAsync(useHttp);

                GraderLogger.Info("Starting client process.");
                _manager.StartClient();
            }
            catch (Exception ex)
            {
                GraderLogger.Error("Failed to start external processes.", ex);
                throw;
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

                if (clientReady && serverReady)
                {
                    return;
                }

                await Task.Delay(200, _token);
            }

            GraderLogger.Warning($"Timeout waiting for stage {stage} results.");
        }

        private async Task<bool> HasClientResultAsync(int stage)
        {
            var result = await Application.Current.Dispatcher.InvokeAsync(() =>
                _recorderContext.OutputClients.LastOrDefault(c => c.Stage == stage));

            return result != null && HasMeaningfulClientExpectation(result);
        }

        private async Task<bool> HasServerResultAsync(int stage)
        {
            var result = await Application.Current.Dispatcher.InvokeAsync(() =>
                _recorderContext.OutputServers.LastOrDefault(c => c.Stage == stage));

            return result != null && HasMeaningfulServerExpectation(result);
        }

        private string EvaluateStage(int stage)
        {
            var issues = new List<string>();

            var expectedClient = _testCase!.ExpectedClients.TryGetValue(stage, out var client) ? client : null;
            var expectedServer = _testCase!.ExpectedServers.TryGetValue(stage, out var server) ? server : null;

            var actualClient = _recorderContext.OutputClients.LastOrDefault(c => c.Stage == stage);
            var actualServer = _recorderContext.OutputServers.LastOrDefault(c => c.Stage == stage);

            EvaluateClient(stage, expectedClient, actualClient, issues);
            EvaluateServer(stage, expectedServer, actualServer, issues);

            if (issues.Count == 0)
            {
                return "PASS";
            }

            foreach (var issue in issues)
            {
                GraderLogger.Warning(issue);
            }

            return string.Join(Environment.NewLine, issues);
        }

        private void EvaluateClient(int stage, OutputClient? expected, OutputClient? actual, List<string> issues)
        {
            if (!HasMeaningfulClientExpectation(expected))
            {
                return;
            }

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
            {
                issues.Add($"Stage {stage}: Client DataResponse mismatch.");
            }

            if (!CompareText(expected.Output, actual.Output))
            {
                issues.Add($"Stage {stage}: Client console output mismatch.");
            }
        }

        private void EvaluateServer(int stage, OutputServer? expected, OutputServer? actual, List<string> issues)
        {
            if (!HasMeaningfulServerExpectation(expected))
            {
                return;
            }

            if (actual == null)
            {
                issues.Add($"Stage {stage}: Missing server output.");
                return;
            }

            CompareString("Server Method", expected!.Method, actual.Method, issues, stage);
            CompareByteSize("Server", expected.ByteSize, actual.ByteSize, issues, stage);
            CompareString("Server DataType", expected.DataTypeMiddleware, actual.DataTypeMiddleware, issues, stage);

            if (!CompareText(expected.DataRequest, actual.DataRequest))
            {
                issues.Add($"Stage {stage}: Server request payload mismatch.");
            }

            if (!CompareText(expected.Output, actual.Output))
            {
                issues.Add($"Stage {stage}: Server console output mismatch.");
            }
        }

        private void CompareString(string field, string expected, string actual, List<string> issues, int stage)
        {
            if (string.Equals(expected?.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            issues.Add($"Stage {stage}: {field} expected '{expected}', actual '{actual}'.");
        }

        private void CompareStatusCode(int expected, int actual, List<string> issues, int stage)
        {
            if (expected == 0 && actual == 0)
            {
                return;
            }

            if (expected != actual)
            {
                issues.Add($"Stage {stage}: StatusCode expected {expected}, actual {actual}.");
            }
        }

        private void CompareByteSize(string scope, int expected, int actual, List<string> issues, int stage)
        {
            if (expected == 0 && actual == 0)
            {
                return;
            }

            if (expected != actual)
            {
                issues.Add($"Stage {stage}: {scope} byte size expected {expected}, actual {actual}.");
            }
        }

        private bool ComparePayload(string expected, string actual, string dataType)
        {
            if (string.IsNullOrWhiteSpace(expected) && string.IsNullOrWhiteSpace(actual))
            {
                return true;
            }

            if (string.Equals(dataType, "JSON", StringComparison.OrdinalIgnoreCase))
            {
                return DataCompare.CompareJson(expected, actual);
            }

            if (string.Equals(dataType, "XML", StringComparison.OrdinalIgnoreCase))
            {
                return DataCompare.CompareXml(expected, actual);
            }

            return CompareText(expected, actual);
        }

        private bool CompareText(string expected, string actual)
        {
            return string.Equals(
                DataCompare.NormalizeData(expected),
                DataCompare.NormalizeData(actual),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Replace("\r", string.Empty).Trim();
        }

        private bool HasMeaningfulClientExpectation(OutputClient? client)
        {
            if (client == null)
            {
                return false;
            }

            return !(string.IsNullOrWhiteSpace(client.Method)
                     && string.IsNullOrWhiteSpace(client.DataResponse)
                     && string.IsNullOrWhiteSpace(client.Output)
                     && string.IsNullOrWhiteSpace(client.DataTypeMiddleWare)
                     && client.StatusCode == 0
                     && client.ByteSize == 0);
        }

        private bool HasMeaningfulServerExpectation(OutputServer? server)
        {
            if (server == null)
            {
                return false;
            }

            return !(string.IsNullOrWhiteSpace(server.Method)
                     && string.IsNullOrWhiteSpace(server.DataRequest)
                     && string.IsNullOrWhiteSpace(server.Output)
                     && string.IsNullOrWhiteSpace(server.DataTypeMiddleware)
                     && server.ByteSize == 0);
        }

        private async Task ExportResultsAsync()
        {
            var exportPath = GetResultExportPath();

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

            GraderLogger.Info($"Exported grading results to {exportPath}");
        }

        private string GetResultExportPath()
        {
            var directory = Path.GetDirectoryName(_config.TestCaseFilePath);
            if (string.IsNullOrEmpty(directory))
            {
                directory = AppDomain.CurrentDomain.BaseDirectory;
            }

            return Path.Combine(directory, "graderesult.xlsx");
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
            {
                errors.Add($"Client executable not found: {_config.ClientPath}");
            }

            if (!File.Exists(_config.ServerPath))
            {
                errors.Add($"Server executable not found: {_config.ServerPath}");
            }

            if (!File.Exists(_config.ClientAppSettingsPath))
            {
                errors.Add($"Client appsettings template not found: {_config.ClientAppSettingsPath}");
            }

            if (!File.Exists(_config.ServerAppSettingsPath))
            {
                errors.Add($"Server appsettings template not found: {_config.ServerAppSettingsPath}");
            }

            if (!File.Exists(_config.TestCaseFilePath))
            {
                errors.Add($"Test case file not found: {_config.TestCaseFilePath}");
            }

            if (errors.Count > 0)
            {
                throw new FileNotFoundException(string.Join(Environment.NewLine, errors));
            }
        }

        private async Task StopAllAsync()
        {
            try
            {
                await _manager.StopAllAsync();
            }
            catch (Exception ex)
            {
                GraderLogger.Error("Error while stopping executables.", ex);
            }

            try
            {
                await _middleware.StopAsync();
            }
            catch (Exception ex)
            {
                GraderLogger.Error("Error while stopping middleware.", ex);
            }
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
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _recorderContext.AppendClientOutput(data);
            });
        }

        private void OnServerOutputReceived(string data)
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _recorderContext.AppendServerOutput(data);
            });
        }

        private Task RunOnUiThreadAsync(Action action)
        {
            return Application.Current.Dispatcher.InvokeAsync(action).Task;
        }
    }
}