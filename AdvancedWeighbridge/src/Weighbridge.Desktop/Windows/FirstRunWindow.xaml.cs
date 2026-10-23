using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Weighbridge.Application;
using Weighbridge.Application.Settings;
using Weighbridge.Desktop.Infrastructure;

namespace Weighbridge.Desktop.Windows;

public partial class FirstRunWindow : Window
{
    public FirstRunWindow()
    {
        InitializeComponent();
        LanguageBox.ItemsSource = Loc.Languages.Select(l => new { l.Code, l.Name }).ToList();
        LanguageBox.SelectedIndex = 0;
        Loaded += (_, _) => CompanyName.Focus();
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedItem is { } item) Loc.Apply((string)item.GetType().GetProperty("Code")!.GetValue(item)!);
    }

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        string? language = LanguageBox.SelectedItem?.GetType().GetProperty("Code")!.GetValue(LanguageBox.SelectedItem) as string;
        if (string.IsNullOrWhiteSpace(CompanyName.Text)) { ErrorText.Text = "Enter the company name."; return; }
        if (AdminPassword.Password != AdminPassword2.Password) { ErrorText.Text = "The passwords do not match."; return; }
        if (!TryKg(Capacity.Text, out var capacity) || !TryKg(Minimum.Text, out var minimum) || !TryKg(Division.Text, out var division))
        {
            ErrorText.Text = "Capacity, minimum and division must be numbers in kilograms.";
            return;
        }
        if (string.IsNullOrWhiteSpace(SiteCode.Text) || string.IsNullOrWhiteSpace(WbCode.Text)) { ErrorText.Text = "Enter the site and weighbridge codes."; return; }

        CreateButton.IsEnabled = false;
        try
        {
            await App.Host.Setup.InitializeAsync(new SetupRequest
            {
                Company = new CompanySettings { Name = CompanyName.Text.Trim(), Address = Blank(CompanyAddress.Text), Phone = Blank(CompanyPhone.Text), TaxNo = Blank(CompanyTax.Text) },
                AdminUsername = AdminUser.Text.Trim(),
                AdminDisplayName = AdminName.Text.Trim(),
                AdminPassword = AdminPassword.Password,
                SiteCode = SiteCode.Text.Trim(),
                SiteName = SiteName.Text.Trim(),
                WeighbridgeCode = WbCode.Text.Trim(),
                WeighbridgeName = WbName.Text.Trim(),
                CapacityKg = capacity,
                MinimumCaptureKg = minimum,
                DivisionKg = division,
                Language = language ?? "en",
            });
            DialogResult = true;
        }
        catch (Exception ex) when (UiErrors.IsUserFacing(ex))
        {
            ErrorText.Text = ex.Message;
        }
        finally
        {
            CreateButton.IsEnabled = true;
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static bool TryKg(string text, out decimal value) => decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value) && value >= 0;
}
