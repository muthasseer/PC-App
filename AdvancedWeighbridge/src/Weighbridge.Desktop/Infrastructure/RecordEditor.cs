using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace Weighbridge.Desktop.Infrastructure;

/// <summary>
/// Edits a settings record (strings, numbers, booleans, enums and nested records) without a hand-written form.
/// Secrets (properties ending in "Protected") and collections are not shown; they have dedicated screens.
/// </summary>
public sealed partial class RecordEditor : UserControl
{
    private readonly List<(PropertyInfo Property, Func<object?> Read)> _fields = [];
    private readonly List<(PropertyInfo Property, RecordEditor Editor)> _children = [];
    private readonly Type _type;
    private readonly object _original;
    private readonly TextBlock _error = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };

    public RecordEditor(object value, IReadOnlySet<string>? hidden = null)
    {
        _original = value;
        _type = value.GetType();
        var panel = new StackPanel();
        foreach (var p in _type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
            if (p.Name.EndsWith("Protected", StringComparison.Ordinal) || hidden?.Contains(p.Name) == true) continue;
            var type = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            var nullable = Nullable.GetUnderlyingType(p.PropertyType) is not null || !p.PropertyType.IsValueType;
            var current = p.GetValue(value);
            var label = Label(p.Name);

            if (type == typeof(bool))
            {
                var box = new CheckBox { Content = label, IsChecked = current as bool? ?? false };
                panel.Children.Add(box);
                _fields.Add((p, () => box.IsChecked == true));
            }
            else if (type.IsEnum)
            {
                var box = new ComboBox { ItemsSource = Enum.GetValues(type), SelectedItem = current, MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
                Add(panel, label, box);
                _fields.Add((p, () => box.SelectedItem));
            }
            else if (type == typeof(string))
            {
                var box = new TextBox { Text = current as string ?? string.Empty, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Stretch };
                Add(panel, label, box);
                _fields.Add((p, () => string.IsNullOrWhiteSpace(box.Text) && nullable ? null : box.Text.Trim()));
            }
            else if (type == typeof(int) || type == typeof(long) || type == typeof(decimal) || type == typeof(double))
            {
                var box = new TextBox { Text = Convert.ToString(current, CultureInfo.CurrentCulture) ?? string.Empty, Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
                Add(panel, label, box);
                _fields.Add((p, () => ParseNumber(box.Text, type, Nullable.GetUnderlyingType(p.PropertyType) is not null, label)));
            }
            else if (IsRecord(type) && current is not null)
            {
                var child = new RecordEditor(current, hidden);
                var expander = new Expander { Header = label, Content = new Border { Padding = new Thickness(16, 4, 0, 4), Child = child }, Margin = new Thickness(0, 6, 0, 0) };
                panel.Children.Add(expander);
                _children.Add((p, child));
            }
        }
        panel.Children.Add(_error);
        Content = panel;
    }

    /// <summary>A copy of the edited record with the new values. Throws <see cref="FormatException"/> for invalid input.</summary>
    public object Build()
    {
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(_original, _type), _type)!;
        foreach (var (p, read) in _fields) p.SetValue(copy, read());
        foreach (var (p, child) in _children) p.SetValue(copy, child.Build());
        return copy;
    }

    public T Build<T>() => (T)Build();

    public void ShowError(string? message)
    {
        _error.Text = message ?? string.Empty;
        _error.Style = (Style)FindResource("Text.Error");
        _error.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void Add(Panel panel, string label, FrameworkElement control)
    {
        panel.Children.Add(new TextBlock { Text = label, Style = (Style)System.Windows.Application.Current.FindResource("Text.Label") });
        panel.Children.Add(control);
        System.Windows.Automation.AutomationProperties.SetName(control, label);
    }

    private static bool IsRecord(Type t) => t.IsClass && t != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null;

    private static object? ParseNumber(string text, Type type, bool nullable, string label)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            if (nullable) return null;
            throw new FormatException($"{label}: enter a number.");
        }
        try
        {
            if (type == typeof(int)) return int.Parse(text, NumberStyles.Integer, CultureInfo.CurrentCulture);
            if (type == typeof(long)) return long.Parse(text, NumberStyles.Integer, CultureInfo.CurrentCulture);
            if (type == typeof(decimal)) return decimal.Parse(text, NumberStyles.Number, CultureInfo.CurrentCulture);
            return double.Parse(text, NumberStyles.Float, CultureInfo.CurrentCulture);
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            throw new FormatException($"{label}: '{text}' is not a valid number.");
        }
    }

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex WordBoundary();

    /// <summary>"FreshnessMs" → "Freshness (ms)", "PrintOnCompletion" → "Print on completion".</summary>
    public static string Label(string name)
    {
        string? unit = null;
        foreach (var (suffix, u) in new[] { ("Ms", "ms"), ("Kg", "kg"), ("Seconds", "s"), ("Minutes", "min"), ("Days", "days") })
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
            {
                name = name[..^suffix.Length];
                unit = u;
                break;
            }
        }
        var words = WordBoundary().Split(name);
        var text = string.Join(' ', words.Select((w, i) => i == 0 ? w : w.Length > 1 && w.All(char.IsUpper) ? w : w.ToLowerInvariant()));
        return unit is null ? text : $"{text} ({unit})";
    }
}
