using System.Windows;
using System.Windows.Controls;

namespace OES_Leak_Monitor;

public partial class MainWindow : Window
{
    // The gated tabs are referenced by their x:Name from MainWindow.xaml, not by index.
    // Hard-coded indices silently pointed at the wrong tab every time a tab was inserted
    // ahead of them — which is exactly how the Configuration gate came to be checking the
    // Leak Calibration tab instead. A name is resolved by the XAML compiler, so inserting
    // tabs can no longer move the gate off its target.

    private int  _previousTabIndex;
    private bool _suppressTabChange;

    public MainWindow()
    {
        InitializeComponent();
        Loaded  += (_, _) =>
        {
            InitializeAccessControl();
            ShowDataFolderWarning();
        };
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
            return;
        }
        // Last chance to tell whoever is standing at the machine that the data drive needs
        // attention — the next shift may only ever see this app being started and stopped.
        ShowDataFolderWarning();
    }

    /// <summary>
    /// Show the data-folder warnings, if any, when the app opens and when it closes. Silent
    /// when there is nothing wrong: a dialog that appears every single time gets dismissed
    /// without being read, which is exactly when it stops working as a warning.
    /// </summary>
    private void ShowDataFolderWarning()
    {
        DataFolderState state;
        try { state = Vm.InspectDataFolder(); }
        catch { return; }   // never let housekeeping block opening or closing the app
        if (state.Warnings.Count == 0) return;

        MessageBox.Show(this,
            string.Join("\n\n", state.Warnings) +
            $"\n\nData folder: {state.BaseDirectory}",
            state.CriticalFreeSpace ? "Data drive almost full" : "Data folder needs attention",
            MessageBoxButton.OK,
            state.CriticalFreeSpace ? MessageBoxImage.Warning : MessageBoxImage.Information);
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

        // If the role just dropped below Engineer while on one of the gated tabs,
        // snap back to Monitor so the user can't keep editing parameters.
        if (role < UserRole.Engineer && IsEngineerGated(MainTabControl.SelectedItem))
        {
            _suppressTabChange = true;
            MainTabControl.SelectedItem = MonitorTab;
            _previousTabIndex = MainTabControl.Items.IndexOf(MonitorTab);
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
    /// Tabs that require Engineer or higher, compared by reference so inserting a tab can
    /// never move the gate onto the wrong one. Replay joins Configuration because it drives
    /// the recorders and the leak-monitor alarm gate.
    /// </summary>
    private bool IsEngineerGated(object? tab) =>
        ReferenceEquals(tab, ConfigurationTab) || ReferenceEquals(tab, ReplayTab);

    /// <summary>
    /// Gate the Engineer-only tabs. If the active user lacks the role, prompt for login;
    /// on cancel, snap the tab back to where it was.
    /// </summary>
    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTabChange) return;
        if (e.Source != MainTabControl) return;

        var newIndex = MainTabControl.SelectedIndex;

        if (IsEngineerGated(MainTabControl.SelectedItem)
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
