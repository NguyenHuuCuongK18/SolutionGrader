using System.Windows;

namespace SolutionGrader.Legacy.Views
{
    public partial class ProgressDialog : Window
    {
        public ProgressDialog(string message)
        {
            InitializeComponent();
            txtStatus.Text = message;
            progressBar.IsIndeterminate = true; // Vòng quay vô hạn, không cần loop
            // Bỏ RunProgress hoàn toàn - dialog close từ bên ngoài
        }
    }
}