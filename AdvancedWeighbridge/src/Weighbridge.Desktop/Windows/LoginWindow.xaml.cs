using System.Windows;
using System.Windows.Documents;
using Weighbridge.Application.Security;
using Weighbridge.Application.Settings;
using Weighbridge.Core.Security;
using Weighbridge.Desktop.Infrastructure;

namespace Weighbridge.Desktop.Windows;

public partial class LoginWindow : Window
{
    private UserContext? _pending;
    private string? _password;

    public LoginWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            UserBox.Focus();
            try { CompanyText.Text = (await App.Host.Settings.GetAsync<CompanySettings>(SettingKeys.Company)).Name; }
            catch (Exception ex) when (UiErrors.IsUserFacing(ex)) { CompanyText.Text = string.Empty; }
        };
        foreach (var (code, name) in Loc.Languages)
        {
            var link = new Hyperlink(new Run(name)) { Tag = code };
            link.Click += (_, _) => Loc.Apply(code);
            if (LanguageLinks.Inlines.Count > 0) LanguageLinks.Inlines.Add(new Run("   "));
            LanguageLinks.Inlines.Add(link);
        }
    }

    public UserContext? User { get; private set; }

    private async void OnSubmit(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        SubmitButton.IsEnabled = false;
        try
        {
            if (_pending is not null)
            {
                await ChangePasswordAsync(_pending);
                return;
            }
            var result = await App.Host.Auth.LoginAsync(UserBox.Text.Trim(), PasswordBox.Password, CodePanel.Visibility == Visibility.Visible ? CodeBox.Text.Trim() : null);
            switch (result.Outcome)
            {
                case LoginOutcome.Success when result.MustChangePassword:
                    _pending = result.User;
                    _password = PasswordBox.Password;
                    CredentialsPanel.Visibility = Visibility.Collapsed;
                    ChangePanel.Visibility = Visibility.Visible;
                    SubmitButton.Content = Loc.T("login.changePassword", "Change password");
                    NewPassword.Focus();
                    break;
                case LoginOutcome.Success:
                    User = result.User;
                    DialogResult = true;
                    break;
                case LoginOutcome.MfaRequired or LoginOutcome.InvalidMfaCode:
                    CodePanel.Visibility = Visibility.Visible;
                    ErrorText.Text = result.Message;
                    CodeBox.Focus();
                    break;
                default:
                    ErrorText.Text = result.Message;
                    PasswordBox.Clear();
                    PasswordBox.Focus();
                    break;
            }
        }
        catch (Exception ex) when (UiErrors.IsUserFacing(ex))
        {
            ErrorText.Text = ex.Message;
        }
        finally
        {
            SubmitButton.IsEnabled = true;
        }
    }

    private async Task ChangePasswordAsync(UserContext user)
    {
        if (NewPassword.Password != NewPassword2.Password)
        {
            ErrorText.Text = "The new passwords do not match.";
            return;
        }
        await App.Host.Auth.ChangePasswordAsync(user, _password!, NewPassword.Password);
        User = user;
        DialogResult = true;
    }
}
