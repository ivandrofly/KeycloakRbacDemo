using System.Windows;
using Consent.Wpf.ViewModels;

namespace Consent.Wpf;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
