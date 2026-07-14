using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using TNovCommon;

namespace TNovBeams
{
    /// <summary>
    /// Логика взаимодействия для BeamsWPF.xaml
    /// </summary>
    public partial class BeamsWPF : Window
    {
        public BeamsWPF(BeamsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        private void acceptButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void escButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            string commandText = HelpLinks.GetHelpLink("Перемычки");
            var proc = new System.Diagnostics.Process();
            proc.StartInfo.FileName = commandText;
            proc.StartInfo.UseShellExecute = true;
            proc.Start();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }
    }
}
