using System.Windows;
using Rbac.Wpf.ViewModels;

namespace Rbac.Wpf;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
