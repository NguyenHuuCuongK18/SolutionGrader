using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SolutionGrader.Models
{
    public class TestStepResult : INotifyPropertyChanged
    {
        private int _step;
        private string _input = string.Empty;
        private string _result = "Pending";

        public int Step
        {
            get => _step;
            set => SetProperty(ref _step, value);
        }

        public string Input
        {
            get => _input;
            set => SetProperty(ref _input, value);
        }

        public string Result
        {
            get => _result;
            set => SetProperty(ref _result, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }
}