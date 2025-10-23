using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SolutionGrader.Models
{
    public class GraderConfig : INotifyPropertyChanged
    {
        private string _clientPath = string.Empty;
        private string _serverPath = string.Empty;
        private string _clientAppSettingsPath = string.Empty;
        private string _serverAppSettingsPath = string.Empty;
        private string _suitePath = string.Empty;
        private string _ignoreFilePath = string.Empty;
        private string _protocol = "HTTP";
        private string _databaseScriptPath = string.Empty;
        private string _resultsDirectory = string.Empty;

        public string ClientPath
        {
            get => _clientPath;
            set => SetProperty(ref _clientPath, value);
        }

        public string ServerPath
        {
            get => _serverPath;
            set => SetProperty(ref _serverPath, value);
        }

        public string ClientAppSettingsPath
        {
            get => _clientAppSettingsPath;
            set => SetProperty(ref _clientAppSettingsPath, value);
        }

        public string ServerAppSettingsPath
        {
            get => _serverAppSettingsPath;
            set => SetProperty(ref _serverAppSettingsPath, value);
        }

        public string SuitePath
        {
            get => _suitePath;
            set => SetProperty(ref _suitePath, value);
        }

        public string IgnoreFilePath
        {
            get => _ignoreFilePath;
            set => SetProperty(ref _ignoreFilePath, value);
        }

        public string Protocol
        {
            get => _protocol;
            set => SetProperty(ref _protocol, value);
        }
        public string DatabaseScriptPath
        {
            get => _databaseScriptPath;
            set => SetProperty(ref _databaseScriptPath, value);
        }

        public string ResultsDirectory
        {
            get => _resultsDirectory;
            set => SetProperty(ref _resultsDirectory, value);
        }


        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged(string? propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}