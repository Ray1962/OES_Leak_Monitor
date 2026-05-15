using System.Windows;
using System.Windows.Controls;

namespace OES_Leak_Monitor;

public partial class MainWindow : Window
{
    private const int MonitorTabIndex       = 0;
    private const int ConfigurationTabIndex = 1;

    private int  _previousTabIndex;
    private bool _suppressTabChange;

    public MainWindow()
    {
        InitializeComponent();
        Loaded  += (_, _) => InitializeAccessControl();
        Closing += MainWindow_Closing;
        Closed  += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.Dispose();
        };
    }

    /// <summary>
    /// Block window close when the active role is Guest. Guests must sign in (any
    /// non-Guest role works) before they can shut the app down. The X button, Alt+F4,
    /// and the system menu all route through this Closing event so they're all gated.
    /// </summary>
    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (Vm.AccessControl.CurrentRole == UserRole.Guest)
        {
            e.Cancel = true;
            MessageBox.Show(this,
                "Sign in is required to close the application.",
                "Permission required", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void InitializeAccessControl()
    {
        Vm.AccessControl.RoleChanged += (_, _) =>
            Dispatcher.BeginInvoke(() => ApplyRolePermissions());

        // Reset auto-lock timer on user activity (mirrors UVLamp_Monitor's pattern).
        PreviewMouseMove += (_, _) => Vm.AccessControl.ResetIdleTimer();
        PreviewKeyDown   += (_, _) => Vm.AccessControl.ResetIdleTimer();

        ApplyRolePermissions();
    }

    /// <summary>
    /// Refresh role-aware UI state. Called from the role-changed event handler and
    /// also after each Login/Logout dialog so the toolbar reflects the current
    /// user even when nested message loops swallow the dispatcher invoke.
    /// </summary>
    private void ApplyRolePermissions()
    {
        var role = Vm.AccessControl.CurrentRole;
        // Sign In is shown when nobody is signed in (Guest); Sign Out replaces it once
        // any user is logged in. Manage Users stays Admin-only.
        LoginButton.Visibility        = role == UserRole.Guest ? Visibility.Visible : Visibility.Collapsed;
        LogoutButton.Visibility       = role >  UserRole.Guest ? Visibility.Visible : Visibility.Collapsed;
        ManageUsersButton.Visibility  = role == UserRole.Admin ? Visibility.Visible : Visibility.Collapsed;

        // If the role just dropped below Engineer while on the Configuration tab,
        // snap back to Monitor so the user can't keep editing parameters.
        if (role < UserRole.Engineer
            && MainTabControl.SelectedIndex == ConfigurationTabIndex)
        {
            _suppressTabChange = true;
            MainTabControl.SelectedIndex = MonitorTabIndex;
            _previousTabIndex = MonitorTabIndex;
            _suppressTabChange = false;
        }
    }

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new LoginDialog(Vm.AccessControl) { Owner = this };
        dialog.ShowDialog();
        ApplyRolePermissions();
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        Vm.AccessControl.Logout();
        ApplyRolePermissions();
    }

    private void ManageUsers_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.AccessControl.CurrentRole < UserRole.Admin)
        {
            // Prompt for Admin login if the button is somehow reached without one.
            var login = new LoginDialog(Vm.AccessControl) { Owner = this };
            login.ShowDialog();
            ApplyRolePermissions();
            if (Vm.AccessControl.CurrentRole < UserRole.Admin) return;
        }

        var users = new UserManagementDialog(Vm.AccessControl) { Owner = this };
        users.ShowDialog();
    }

    /// <summary>
    /// Gate the Configuration tab on Engineer or higher. If the active user lacks
    /// the role, prompt for login; on cancel, snap the tab back to Monitor.
    /// </summary>
    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTabChange) return;
        if (e.Source != MainTabControl) return;

        var newIndex = MainTabControl.SelectedIndex;

        if (newIndex == ConfigurationTabIndex
            && Vm.AccessControl.CurrentRole < UserRole.Engineer)
        {
            if (!TryRequireEngineer())
            {
                _suppressTabChange = true;
                MainTabControl.SelectedIndex = _previousTabIndex;
                _suppressTabChange = false;
                return;
            }
        }
        _previousTabIndex = newIndex;
    }

    private bool TryRequireEngineer()
    {
        if (Vm.AccessControl.CurrentRole >= UserRole.Engineer) return true;

        var dialog = new LoginDialog(Vm.AccessControl) { Owner = this };
        var result = dialog.ShowDialog();
        ApplyRolePermissions();
        return result == true && Vm.AccessControl.CurrentRole >= UserRole.Engineer;
    }
}
