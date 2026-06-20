using System.Windows;
using QBLockManager.Services;

namespace QBLockManager.Views;

public partial class AuditLogWindow : Window
{
    public AuditLogWindow(List<AuditLogEntryDto> entries)
    {
        InitializeComponent();
        Grid.ItemsSource = entries;
    }
}
