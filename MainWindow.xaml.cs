using System.Windows;
using SolutionGrader.ViewModels;

namespace SolutionGrader
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }
    }
}