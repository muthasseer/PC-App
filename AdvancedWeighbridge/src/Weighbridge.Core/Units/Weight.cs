using System.Globalization;

namespace Weighbridge.Core.Units;

public enum WeightUnit
{
    Kilogram = 0,
    Tonne = 1,
    Pound = 2,
    Gram = 3,
}

public static class WeightUnits
{
    /// <summary>Exact international avoirdupois pound.</summary>
    public const decimal KilogramsPerPound = 0.45359237m;

    public static decimal ToKilograms(decimal value, WeightUnit unit) => unit switch
    {
        WeightUnit.Kilogram => value,
        WeightUnit.Tonne => value * 1000m,
        WeightUnit.Pound => value * KilogramsPerPound,
        WeightUnit.Gram => value / 1000m,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null),
    };

    public static decimal FromKilograms(decimal kilograms, WeightUnit unit) => unit switch
    {
        WeightUnit.Kilogram => kilograms,
        WeightUnit.Tonne => kilograms / 1000m,
        WeightUnit.Pound => kilograms / KilogramsPerPound,
        WeightUnit.Gram => kilograms * 1000m,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null),
    };

    public static string Symbol(WeightUnit unit) => unit switch
    {
        WeightUnit.Kilogram => "kg",
        WeightUnit.Tonne => "t",
        WeightUnit.Pound => "lb",
        WeightUnit.Gram => "g",
        _ => "?",
    };

    private static readonly Dictionary<string, WeightUnit> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kg"] = WeightUnit.Kilogram,
        ["kgs"] = WeightUnit.Kilogram,
        ["kilogram"] = WeightUnit.Kilogram,
        ["kilograms"] = WeightUnit.Kilogram,
        ["t"] = WeightUnit.Tonne,
        ["tn"] = WeightUnit.Tonne,
        ["ton"] = WeightUnit.Tonne,
        ["tons"] = WeightUnit.Tonne,
        ["tonne"] = WeightUnit.Tonne,
        ["tonnes"] = WeightUnit.Tonne,
        ["mt"] = WeightUnit.Tonne,
        ["lb"] = WeightUnit.Pound,
        ["lbs"] = WeightUnit.Pound,
        ["g"] = WeightUnit.Gram,
        ["gr"] = WeightUnit.Gram,
    };

    /// <summary>Recognises the unit spellings indicators commonly print (case-insensitive).</summary>
    public static bool TryParse(string? text, out WeightUnit unit)
    {
        unit = WeightUnit.Kilogram;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Aliases.TryGetValue(text.Trim(), out unit);
    }

    public static IReadOnlyCollection<string> KnownSymbols => Aliases.Keys;
}

/// <summary>
/// A mass held in kilograms with full decimal precision. Persisted as integer grams so that database
/// comparisons and sums are exact.
/// </summary>
public readonly record struct Weight(decimal Kilograms) : IComparable<Weight>
{
    public static readonly Weight Zero = new(0m);

    public static Weight FromKilograms(decimal kg) => new(kg);
    public static Weight From(decimal value, WeightUnit unit) => new(WeightUnits.ToKilograms(value, unit));
    public static Weight FromGrams(long grams) => new(grams / 1000m);

    public long ToGrams() => (long)decimal.Round(Kilograms * 1000m, 0, MidpointRounding.AwayFromZero);

    public decimal In(WeightUnit unit) => WeightUnits.FromKilograms(Kilograms, unit);

    public bool IsNegative => Kilograms < 0;

    public static Weight operator +(Weight a, Weight b) => new(a.Kilograms + b.Kilograms);
    public static Weight operator -(Weight a, Weight b) => new(a.Kilograms - b.Kilograms);
    public static Weight operator -(Weight a) => new(-a.Kilograms);
    public static bool operator <(Weight a, Weight b) => a.Kilograms < b.Kilograms;
    public static bool operator >(Weight a, Weight b) => a.Kilograms > b.Kilograms;
    public static bool operator <=(Weight a, Weight b) => a.Kilograms <= b.Kilograms;
    public static bool operator >=(Weight a, Weight b) => a.Kilograms >= b.Kilograms;

    public static Weight Max(Weight a, Weight b) => a >= b ? a : b;
    public static Weight Min(Weight a, Weight b) => a <= b ? a : b;
    public Weight Abs() => new(Math.Abs(Kilograms));

    public int CompareTo(Weight other) => Kilograms.CompareTo(other.Kilograms);

    public string Format(WeightDisplayOptions options) => options.Format(this);

    public override string ToString() => Kilograms.ToString("0.###", CultureInfo.InvariantCulture) + " kg";
}

/// <summary>How weights are shown on screen, tickets and reports.</summary>
public sealed record WeightDisplayOptions
{
    public WeightUnit Unit { get; init; } = WeightUnit.Kilogram;

    /// <summary>Number of decimals shown. Weighbridges typically use 0 for kg and 2-3 for tonnes.</summary>
    public int Decimals { get; init; }

    public bool ShowUnit { get; init; } = true;
    public bool UseThousandsSeparator { get; init; } = true;
    public string CultureName { get; init; } = "en-US";

    public static WeightDisplayOptions DefaultKg { get; } = new();
    public static WeightDisplayOptions DefaultTonnes { get; } = new() { Unit = WeightUnit.Tonne, Decimals = 2 };

    public string FormatNumber(Weight weight)
    {
        var culture = SafeCulture(CultureName);
        var value = decimal.Round(weight.In(Unit), Decimals, MidpointRounding.AwayFromZero);
        var pattern = (UseThousandsSeparator ? "#,0" : "0") + (Decimals > 0 ? "." + new string('0', Decimals) : string.Empty);
        return value.ToString(pattern, culture);
    }

    public string Format(Weight weight)
    {
        var number = FormatNumber(weight);
        return ShowUnit ? number + " " + WeightUnits.Symbol(Unit) : number;
    }

    internal static CultureInfo SafeCulture(string name)
    {
        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
