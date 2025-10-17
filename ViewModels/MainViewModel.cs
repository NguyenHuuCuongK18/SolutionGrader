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
using Microsoft.Win32;
using SolutionGrader.Commands;
using SolutionGrader.Models;
using SolutionGrader.Recorder;
using SolutionGrader.Services;
using SolutionGrader.Legacy.Model;
using SolutionGrader.Legacy.Service;

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
            BrowseTestCaseFileCommand = new RelayCommand(() => BrowseExcel(path => Config.TestCaseFilePath = path));
            BrowseIgnoreFileCommand = new RelayCommand(() => BrowseExcel(path => Config.IgnoreFilePath = path));

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
        public ICommand BrowseTestCaseFileCommand { get; }
        public ICommand BrowseIgnoreFileCommand { get; }
        public ICommand RunGraderCommand { get; }
        public ICommand CancelCommand { get; }

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
            if (IsRunning)
            {
                return;
            }

            try
            {
                IsRunning = true;
                _cancellationTokenSource = new CancellationTokenSource();

                var ignoreList = LoadIgnoreList(Config.IgnoreFilePath);
                var recorderContext = new GraderRecorderContext(ignoreList);
                AttachRecorderContext(recorderContext);

                var session = new GradingSession(Config, TestSteps, StudentOutputClient, StudentOutputServer, recorderContext);
                var result = await session.RunAsync(_cancellationTokenSource.Token);

                if (result.Success)
                {
                    var message = result.ExportPath != null
                        ? $"Grading completed successfully.\nResult exported to:{Environment.NewLine}{result.ExportPath}"
                        : "Grading completed successfully.";
                    MessageBox.Show(message, "Solution Grader", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (result.Cancelled)
                {
                    MessageBox.Show("Grading cancelled.", "Solution Grader", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(result.Message, "Solution Grader", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                MessageBox.Show("Grading cancelled.", "Solution Grader", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                GraderLogger.Error("Unexpected error while running grader.", ex);
                MessageBox.Show($"Grading failed: {ex.Message}", "Solution Grader", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsRunning = false;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        private void Cancel()
        {
            if (!IsRunning)
            {
                return;
            }

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
            if (_recorderContext == null)
            {
                return;
            }

            SyncList(_recorderContext.OutputClients, StudentOutputClient, e);
        }

        private void OnServerListChanged(object? sender, ListChangedEventArgs e)
        {
            if (_recorderContext == null)
            {
                return;
            }

            SyncList(_recorderContext.OutputServers, StudentOutputServer, e);
        }

        private void SyncList<T>(BindingList<T> source, ObservableCollection<T> target, ListChangedEventArgs e)
        {
            switch (e.ListChangedType)
            {
                case ListChangedType.Reset:
                    target.Clear();
                    foreach (var item in source)
                    {
                        target.Add(item);
                    }
                    break;
                case ListChangedType.ItemAdded:
                    if (e.NewIndex >= 0 && e.NewIndex <= target.Count)
                    {
                        target.Insert(e.NewIndex, source[e.NewIndex]);
                    }
                    break;
                case ListChangedType.ItemChanged:
                    if (e.NewIndex >= 0 && e.NewIndex < target.Count)
                    {
                        target[e.NewIndex] = source[e.NewIndex];
                    }
                    break;
                case ListChangedType.ItemDeleted:
                    if (e.NewIndex >= 0 && e.NewIndex < target.Count)
                    {
                        target.RemoveAt(e.NewIndex);
                    }
                    break;
                default:
                    target.Clear();
                    foreach (var item in source)
                    {
                        target.Add(item);
                    }
                    break;
            }
        }

        private static HashSet<string> LoadIgnoreList(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                return IgnoreListLoader.IgnoreLoader(path);
            }
            catch (Exception ex)
            {
                GraderLogger.Error("Failed to load ignore list.", ex);
                MessageBox.Show($"Unable to load ignore list: {ex.Message}", "Solution Grader", MessageBoxButton.OK, MessageBoxImage.Warning);
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void BrowseClient()
        {
            BrowseExecutable(path => Config.ClientPath = path);
        }

        private void BrowseServer()
        {
            BrowseExecutable(path => Config.ServerPath = path);
        }

        private void BrowseExecutable(Action<string> setter)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                setter(dialog.FileName);
            }
        }

        private void BrowseJson(Action<string> setter)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                setter(dialog.FileName);
            }
        }

        private void BrowseExcel(Action<string> setter)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Excel Files (*.xlsx)|*.xlsx|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                setter(dialog.FileName);
            }
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}