using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
// NOTE: do NOT import Microsoft.Win32 or System.Windows.Forms here without qualification
using SolutionGrader.Commands;
using SolutionGrader.Models;
using SolutionGrader.Recorder;
using SolutionGrader.Services;
using SolutionGrader.Legacy.Model;
using SolutionGrader.Legacy.Service;
using Forms = System.Windows.Forms;

namespace SolutionGrader.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly AsyncRelayCommand _runCommand;
        private readonly RelayCommand _cancelCommand;
        private CancellationTokenSource? _cancellationTokenSource;
        private GraderRecorderContext? _recorderContext;
        private bool _isRunning;

        public MainViewModel()
        {
            Config = new GraderConfig();
            TestSteps = new ObservableCollection<TestStepResult>();
            StudentOutputClient = new ObservableCollection<OutputClient>();
            StudentOutputServer = new ObservableCollection<OutputServer>();

            BrowseClientCommand = new RelayCommand(BrowseClient);
            BrowseServerCommand = new RelayCommand(BrowseServer);
            BrowseClientAppSettingsCommand = new RelayCommand(() => BrowseJson(path => Config.ClientAppSettingsPath = path));
            BrowseServerAppSettingsCommand = new RelayCommand(() => BrowseJson(path => Config.ServerAppSettingsPath = path));
            BrowseSuiteFileCommand = new RelayCommand(() => BrowseExcel(path => Config.SuitePath = path));
            BrowseSuiteFolderCommand = new RelayCommand(BrowseSuiteFolder);
            BrowseIgnoreFileCommand = new RelayCommand(() => BrowseExcel(path => Config.IgnoreFilePath = path));
            BrowseDatabaseScriptCommand = new RelayCommand(() => BrowseSql(path => Config.DatabaseScriptPath = path));
            BrowseResultsDirectoryCommand = new RelayCommand(BrowseResultsDirectory);
            _runCommand = new AsyncRelayCommand(RunGraderAsync, () => !IsRunning);
            _cancelCommand = new RelayCommand(Cancel, () => IsRunning);

            RunGraderCommand = _runCommand;
            CancelCommand = _cancelCommand;
        }

        public GraderConfig Config { get; }
        public ObservableCollection<TestStepResult> TestSteps { get; }
        public ObservableCollection<OutputClient> StudentOutputClient { get; }
        public ObservableCollection<OutputServer> StudentOutputServer { get; }

        public ICommand BrowseClientCommand { get; }
        public ICommand BrowseServerCommand { get; }
        public ICommand BrowseClientAppSettingsCommand { get; }
        public ICommand BrowseServerAppSettingsCommand { get; }
        public ICommand BrowseSuiteFileCommand { get; }
        public ICommand BrowseSuiteFolderCommand { get; }
        public ICommand BrowseIgnoreFileCommand { get; }
        public ICommand RunGraderCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand BrowseDatabaseScriptCommand { get; }
        public ICommand BrowseResultsDirectoryCommand { get; }

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (_isRunning != value)
                {
                    _isRunning = value;
                    OnPropertyChanged(nameof(IsRunning));
                    _runCommand.RaiseCanExecuteChanged();
                    _cancelCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private async Task RunGraderAsync()
        {
            // Chặn re-entrancy (ấn nhiều lần / đổi test case khi đang chạy)
            if (IsRunning) return;

            try
            {
                IsRunning = true;
                StatusMessage = "Starting grading...";

                _cancellationTokenSource = new CancellationTokenSource();

                // Tạo context theo ignore list
                var ignoreList = LoadIgnoreList(Config.IgnoreFilePath);
                var recorderContext = new GraderRecorderContext(ignoreList);
                AttachRecorderContext(recorderContext);

                var session = new GradingSession(
                    Config,
                    TestSteps,
                    StudentOutputClient,
                    StudentOutputServer,
                    recorderContext);

                var result = await session.RunAsync(_cancellationTokenSource.Token);

                if (result.Cancelled)
                {
                    StatusMessage = "Grading cancelled by user.";
                    GraderLogger.Info("Grading cancelled.");
                    return; // KHÔNG hiện dialog
                }

                if (result.Success)
                {
                    var msg = result.ExportPath != null
                        ? $"Grading completed. Results at: {result.ExportPath}"
                        : "Grading completed.";
                    StatusMessage = msg;
                    GraderLogger.Info(msg);
                    // KHÔNG hiện dialog khi OK
                }
                else
                {
                    // Thất bại: ghi log + status, KHÔNG spam dialog
                    StatusMessage = $"Grading failed: {result.Message}";
                    GraderLogger.Warning(StatusMessage);
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Grading cancelled.";
                GraderLogger.Info("Grading cancelled (OperationCanceledException).");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Grading failed: {ex.Message}";
                GraderLogger.Error(StatusMessage, ex);
            }
            finally
            {
                IsRunning = false;
                _runCommand.RaiseCanExecuteChanged();
                _cancelCommand.RaiseCanExecuteChanged();
            }
        }

        private void Cancel()
        {
            if (!IsRunning) return;
            _cancellationTokenSource?.Cancel();
        }

        private void AttachRecorderContext(GraderRecorderContext context)
        {
            if (_recorderContext != null)
            {
                _recorderContext.OutputClients.ListChanged -= OnClientListChanged;
                _recorderContext.OutputServers.ListChanged -= OnServerListChanged;
            }

            _recorderContext = context;
            _recorderContext.OutputClients.ListChanged += OnClientListChanged;
            _recorderContext.OutputServers.ListChanged += OnServerListChanged;
        }

        private void OnClientListChanged(object? sender, ListChangedEventArgs e)
        {
            if (_recorderContext == null) return;
            SyncList(_recorderContext.OutputClients, StudentOutputClient, e);
        }

        private void OnServerListChanged(object? sender, ListChangedEventArgs e)
        {
            if (_recorderContext == null) return;
            SyncList(_recorderContext.OutputServers, StudentOutputServer, e);
        }

        private void SyncList<T>(System.ComponentModel.BindingList<T> source, ObservableCollection<T> target, ListChangedEventArgs e)
        {
            switch (e.ListChangedType)
            {
                case ListChangedType.Reset:
                    target.Clear();
                    foreach (var item in source) target.Add(item);
                    break;
                case ListChangedType.ItemAdded:
                    if (e.NewIndex >= 0 && e.NewIndex <= target.Count) target.Insert(e.NewIndex, source[e.NewIndex]);
                    break;
                case ListChangedType.ItemChanged:
                    if (e.NewIndex >= 0 && e.NewIndex < target.Count) target[e.NewIndex] = source[e.NewIndex];
                    break;
                case ListChangedType.ItemDeleted:
                    if (e.NewIndex >= 0 && e.NewIndex < target.Count) target.RemoveAt(e.NewIndex);
                    break;
                default:
                    target.Clear();
                    foreach (var item in source) target.Add(item);
                    break;
            }
        }

        private static HashSet<string> LoadIgnoreList(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                return IgnoreListLoader.IgnoreLoader(path);
            }
            catch (Exception ex)
            {
                GraderLogger.Error("Failed to load ignore list.", ex);
                System.Windows.MessageBox.Show($"Unable to load ignore list: {ex.Message}", "Solution Grader",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void BrowseClient() => BrowseExecutable(path => Config.ClientPath = path);
        private void BrowseServer() => BrowseExecutable(path => Config.ServerPath = path);

        private void BrowseExecutable(Action<string> setter)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*"
            };
            if (dialog.ShowDialog() == true) setter(dialog.FileName);
        }

        private void BrowseJson(Action<string> setter)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*"
            };
            if (dialog.ShowDialog() == true) setter(dialog.FileName);
        }

        private void BrowseExcel(Action<string> setter)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Excel Files (*.xlsx)|*.xlsx|All Files (*.*)|*.*"
            };
            if (dialog.ShowDialog() == true) setter(dialog.FileName);
        }

        private void BrowseSql(Action<string> setter)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "SQL Files (*.sql)|*.sql|All Files (*.*)|*.*"
            };
            if (dialog.ShowDialog() == true) setter(dialog.FileName);
        }

        private void BrowseSuiteFolder()
        {
            using var dialog = new Forms.FolderBrowserDialog
            {
                Description = "Select the folder that contains the test suite header.",
                ShowNewFolderButton = false
            };
            if (dialog.ShowDialog() == Forms.DialogResult.OK)
                Config.SuitePath = dialog.SelectedPath;
        }

        private void BrowseResultsDirectory()
        {
            using var dialog = new Forms.FolderBrowserDialog
            {
                Description = "Select the folder where grading results should be stored.",
                ShowNewFolderButton = true
            };
            if (dialog.ShowDialog() == Forms.DialogResult.OK)
                Config.ResultsDirectory = dialog.SelectedPath;
        }

        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        private string _statusMessage;
        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                if (_statusMessage != value)
                {
                    _statusMessage = value;
                    OnPropertyChanged(nameof(StatusMessage));
                }
            }
        }

    }
}
