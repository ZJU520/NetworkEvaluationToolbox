using System.Windows;

namespace NetToolbox;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        LogBox.TextChanged += (_, _) => LogBox.ScrollToEnd();
    }
}
