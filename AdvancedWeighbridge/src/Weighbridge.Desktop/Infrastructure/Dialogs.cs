using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace Weighbridge.Desktop.Infrastructure;

/// <summary>A small modal form built in code: labelled fields, an error line and OK/Cancel.</summary>
public sealed class FormDialog : Window
{
    private readonly StackPanel _fields = new();
    private readonly TextBlock _error;
    private readonly Button _ok;

    public FormDialog(string title, string? message = null, string okText = "OK", bool danger = false)
    {
        Title = title;
        Style = (Style)System.Windows.Application.Current.FindResource("Window.Base");
        Owner = Dialogs.Owner;
        WindowStartupLocation = Owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.Height;
        Width = 460;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Text.H2"), Margin = new Thickness(0, 0, 0, 8) });
        if (!string.IsNullOrWhiteSpace(message)) root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        root.Children.Add(_fields);
        _error = new TextBlock { Style = (Style)FindResource("Text.Error"), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        root.Children.Add(_error);
        _ok = new Button { Content = okText, IsDefault = true, Style = (Style)FindResource(danger ? "Button.Danger" : "Button.Primary") };
        _ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = Loc.T("common.cancel", "Cancel"), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(_ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) =>
        {
            var first = _fields.Children.OfType<FrameworkElement>().Select(FirstInput).FirstOrDefault(c => c is not null);
            (first ?? _ok).Focus();
        };
    }

    /// <summary>Returns an error message to keep the dialog open, or null to accept.</summary>
    public Func<string?>? Validate { get; set; }

    public TextBox AddText(string label, string value = "", bool multiline = false, int maxLength = 500)
    {
        var box = new TextBox { Text = value, MaxLength = maxLength };
        if (multiline)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.Height = 70;
            box.VerticalContentAlignment = VerticalAlignment.Top;
        }
        Add(label, box);
        return box;
    }

    public PasswordBox AddPassword(string label)
    {
        var box = new PasswordBox();
        Add(label, box);
        return box;
    }

    public ComboBox AddCombo<T>(string label, IEnumerable<T> items, T? selected, string? displayMember = null)
    {
        var box = new ComboBox { ItemsSource = items.ToList(), SelectedItem = selected, DisplayMemberPath = displayMember };
        Add(label, box);
        return box;
    }

    public CheckBox AddCheck(string label, bool value)
    {
        var box = new CheckBox { Content = label, IsChecked = value };
        _fields.Children.Add(box);
        return box;
    }

    public void AddNote(string text, bool warning = false) => _fields.Children.Add(new TextBlock
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 4),
        Style = (Style)FindResource(warning ? "Text.Error" : "Text.Muted"),
    });

    public void Add(string label, FrameworkElement control)
    {
        _fields.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Text.Label") });
        _fields.Children.Add(control);
        AutomationPropertiesHelper.SetName(control, label);
    }

    public bool ShowModal() => ShowDialog() == true;

    public void ShowError(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
    }

    private void Accept()
    {
        var problem = Validate?.Invoke();
        if (problem is not null)
        {
            ShowError(problem);
            return;
        }
        DialogResult = true;
    }

    private static Control? FirstInput(FrameworkElement e) => e is TextBox or PasswordBox or ComboBox ? (Control)e : null;
}

internal static class AutomationPropertiesHelper
{
    public static void SetName(DependencyObject element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);
}

public sealed record SupervisorCredentials(string Username, string Password, string Reason);

/// <summary>Standard message boxes and prompts, owned by the active window.</summary>
public static class Dialogs
{
    public static Window? Owner
    {
        get
        {
            var app = System.Windows.Application.Current;
            if (app is null) return null;
            return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? (app.MainWindow is { IsVisible: true } m ? m : null);
        }
    }

    public static void Info(string message, string? title = null) => Show(message, title ?? Loc.T("app.title"), MessageBoxImage.Information);

    public static void Warning(string message, string? title = null) => Show(message, title ?? Loc.T("app.title"), MessageBoxImage.Warning);

    public static void Error(string message, string? title = null) => Show(message, title ?? Loc.T("app.title"), MessageBoxImage.Error);

    /// <summary>
    /// Set by unattended runs (screenshots): messages are reported here instead of opening a modal box, and
    /// confirmations answer "No". Interactive use never sets it.
    /// </summary>
    public static Action<MessageBoxImage, string>? Unattended { get; set; }

    public static bool Confirm(string message, string? title = null)
    {
        if (Unattended is { } report)
        {
            report(MessageBoxImage.Question, message);
            return false;
        }
        var owner = Owner;
        var r = owner is null
            ? MessageBox.Show(message, title ?? Loc.T("app.title"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(owner, message, title ?? Loc.T("app.title"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        return r == MessageBoxResult.Yes;
    }

    private static void Show(string message, string title, MessageBoxImage image)
    {
        if (Unattended is { } report)
        {
            report(image, message);
            return;
        }
        var owner = Owner;
        if (owner is null) MessageBox.Show(message, title, MessageBoxButton.OK, image);
        else MessageBox.Show(owner, message, title, MessageBoxButton.OK, image);
    }

    /// <summary>Asks for a text (for example a reason). Returns null when cancelled.</summary>
    public static string? Prompt(string title, string message, string initial = "", int minLength = 0, bool multiline = false, string? label = null)
    {
        var d = new FormDialog(title, message);
        var box = d.AddText(label ?? Loc.T("common.reason", "Reason"), initial, multiline);
        d.Validate = () => box.Text.Trim().Length < minLength ? $"Enter at least {minLength} characters." : null;
        return d.ShowModal() ? box.Text.Trim() : null;
    }

    /// <summary>Supervisor sign-in for actions that need a second person. The reason is optional when minReason is 0.</summary>
    public static SupervisorCredentials? Supervisor(string action, int minReason, string? reasonLabel = null)
    {
        var d = new FormDialog(Loc.T("sup.title", "Supervisor authorisation"), $"{action}\n{Loc.T("sup.hint", "A supervisor must sign in to approve this action.")}");
        var user = d.AddText(Loc.T("login.username", "User name"));
        var pass = d.AddPassword(Loc.T("login.password", "Password"));
        TextBox? reason = minReason > 0 || reasonLabel is not null ? d.AddText(reasonLabel ?? Loc.T("common.reason", "Reason"), multiline: true) : null;
        d.Validate = () =>
        {
            if (string.IsNullOrWhiteSpace(user.Text) || string.IsNullOrEmpty(pass.Password)) return "Enter the supervisor's user name and password.";
            if (reason is not null && reason.Text.Trim().Length < minReason) return $"Enter a reason of at least {minReason} characters.";
            return null;
        };
        return d.ShowModal() ? new SupervisorCredentials(user.Text.Trim(), pass.Password, reason?.Text.Trim() ?? string.Empty) : null;
    }

    public static string? SaveFile(string filter, string fileName, string? folder = null)
    {
        var d = new SaveFileDialog { Filter = filter, FileName = fileName, InitialDirectory = folder ?? string.Empty, AddExtension = true, OverwritePrompt = true };
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public static string? OpenFile(string filter, string? folder = null)
    {
        var d = new OpenFileDialog { Filter = filter, InitialDirectory = folder ?? string.Empty, CheckFileExists = true };
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public static string? PickFolder(string title, string? initial = null)
    {
        var d = new OpenFolderDialog { Title = title, InitialDirectory = initial ?? string.Empty };
        return d.ShowDialog(Owner) == true ? d.FolderName : null;
    }

    /// <summary>Runs an action and shows any expected error as a message instead of crashing.</summary>
    public static async Task RunAsync(Func<Task> action, string? context = null)
    {
        var owner = Owner;
        if (owner is not null) owner.Cursor = Cursors.Wait;
        try
        {
            await action();
        }
        catch (Exception e) when (UiErrors.IsUserFacing(e))
        {
            Error(e.Message, context);
        }
        finally
        {
            if (owner is not null) owner.Cursor = null;
        }
    }
}

public static class UiErrors
{
    /// <summary>Errors that carry a message meant for the operator (validation, permissions, concurrency, I/O).</summary>
    public static bool IsUserFacing(Exception e) => e is ArgumentException or InvalidOperationException or UnauthorizedAccessException
        or Core.Security.PermissionDeniedException or Data.ConcurrencyException or Application.Weighing.ApprovalRequiredException
        or IOException or FormatException or TimeoutException or System.Data.Common.DbException or HttpRequestException
        or System.Security.Cryptography.CryptographicException or KeyNotFoundException;
}
