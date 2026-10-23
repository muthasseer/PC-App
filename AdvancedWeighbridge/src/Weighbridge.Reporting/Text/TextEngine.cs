using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Weighbridge.Reporting.Text;

public enum Script { Latin, Sinhala, Tamil }

public enum TextAlign { Left, Center, Right }

public sealed record TextStyle(float Size, bool Bold = false, SKColor? Color = null, bool Mono = false)
{
    public SKColor Ink => Color ?? SKColors.Black;
}

/// <summary>The bundled Noto fonts (embedded resources), so output is identical on every machine.</summary>
public sealed class FontLibrary
{
    private static readonly Lazy<FontLibrary> Instance = new(() => new FontLibrary());
    private readonly Dictionary<string, SKTypeface> _faces = new(StringComparer.Ordinal);

    private FontLibrary()
    {
        var assembly = typeof(FontLibrary).Assembly;
        foreach (var name in new[] { "NotoSans-Regular", "NotoSans-Bold", "NotoSansSinhala-Regular", "NotoSansSinhala-Bold", "NotoSansTamil-Regular", "NotoSansTamil-Bold", "NotoSansMono-Regular", "NotoSansMono-Bold" })
        {
            var resource = assembly.GetManifestResourceNames().Single(r => r.EndsWith($".Fonts.{name}.ttf", StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            _faces[name] = SKTypeface.FromData(SKData.CreateCopy(ms.ToArray())) ?? throw new InvalidOperationException($"Font {name} could not be loaded.");
        }
    }

    public static FontLibrary Shared => Instance.Value;

    public SKTypeface Face(Script script, bool bold, bool mono = false)
    {
        var weight = bold ? "Bold" : "Regular";
        var family = script switch
        {
            Script.Sinhala => "NotoSansSinhala",
            Script.Tamil => "NotoSansTamil",
            _ => mono ? "NotoSansMono" : "NotoSans",
        };
        return _faces[$"{family}-{weight}"];
    }
}

/// <summary>
/// Draws and measures mixed-script text (English + Sinhala + Tamil) with HarfBuzz shaping, picking the right Noto
/// face per run. Works on any SKCanvas (PDF pages and bitmaps alike).
/// </summary>
public sealed class TextEngine
{
    private static readonly Lazy<TextEngine> Instance = new(() => new TextEngine(FontLibrary.Shared));
    private readonly FontLibrary _fonts;
    private readonly Dictionary<SKTypeface, SKShaper> _shapers = [];
    private readonly object _gate = new();

    public TextEngine(FontLibrary fonts) => _fonts = fonts;

    public static TextEngine Shared => Instance.Value;

    public static Script ScriptOf(char c) => c switch
    {
        >= '඀' and <= '෿' => Script.Sinhala,
        >= '஀' and <= '௿' => Script.Tamil,
        _ => Script.Latin,
    };

    /// <summary>
    /// Splits text into runs of one script. Spaces and zero-width joiners stay with the current run; digits and
    /// punctuation belong to the Latin face (the Sinhala/Tamil Noto faces do not contain every symbol, e.g. "·").
    /// </summary>
    public static IReadOnlyList<(string Text, Script Script)> Segment(string text)
    {
        var runs = new List<(string, Script)>();
        if (string.IsNullOrEmpty(text)) return runs;
        var start = 0;
        var current = (Script?)null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var s = ScriptOf(c);
            var neutral = c is '‌' or '‍' or ' ';
            if (neutral || (current is not null && s == current)) continue;
            if (current is null)
            {
                current = s;
                continue;
            }
            runs.Add((text[start..i], current.Value));
            start = i;
            current = s;
        }
        runs.Add((text[start..], current ?? Script.Latin));
        return runs;
    }

    public float Measure(string text, TextStyle style)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        lock (_gate)
        {
            var width = 0f;
            foreach (var (run, script) in Segment(text))
            {
                var face = _fonts.Face(script, style.Bold, style.Mono);
                using var font = new SKFont(face, style.Size);
                width += Shaper(face).Shape(run, font).Width;
            }
            return width;
        }
    }

    /// <summary>Draws text with its baseline at <paramref name="baseline"/>. Returns the drawn width.</summary>
    public float Draw(SKCanvas canvas, string text, float x, float baseline, TextStyle style, TextAlign align = TextAlign.Left)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var width = Measure(text, style);
        var left = align switch
        {
            TextAlign.Center => x - width / 2,
            TextAlign.Right => x - width,
            _ => x,
        };
        lock (_gate)
        {
            using var paint = new SKPaint { Color = style.Ink, IsAntialias = true };
            foreach (var (run, script) in Segment(text))
            {
                var face = _fonts.Face(script, style.Bold, style.Mono);
                using var font = new SKFont(face, style.Size) { Subpixel = true };
                var shaper = Shaper(face);
                canvas.DrawShapedText(shaper, run, left, baseline, SKTextAlign.Left, font, paint);
                left += shaper.Shape(run, font).Width;
            }
        }
        return width;
    }

    /// <summary>Distance from the top of a line to its baseline, and the line height, for a style.</summary>
    public static (float Ascent, float LineHeight) Metrics(TextStyle style) => (style.Size * 1.0f, style.Size * 1.38f);

    public IReadOnlyList<string> Wrap(string text, float maxWidth, TextStyle style)
    {
        var lines = new List<string>();
        foreach (var paragraph in (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var words = paragraph.Split(' ');
            var line = string.Empty;
            foreach (var word in words)
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (Measure(candidate, style) <= maxWidth || line.Length == 0)
                {
                    line = candidate;
                    // A single word longer than the line is cut.
                    while (Measure(line, style) > maxWidth && line.Length > 1)
                    {
                        var cut = line.Length - 1;
                        while (cut > 1 && Measure(line[..cut], style) > maxWidth) cut--;
                        lines.Add(line[..cut]);
                        line = line[cut..];
                    }
                }
                else
                {
                    lines.Add(line);
                    line = word;
                }
            }
            lines.Add(line);
        }
        return lines;
    }

    public string Ellipsize(string text, float maxWidth, TextStyle style)
    {
        if (Measure(text, style) <= maxWidth) return text;
        var t = text;
        while (t.Length > 0 && Measure(t + "…", style) > maxWidth) t = t[..^1];
        return t + "…";
    }

    private SKShaper Shaper(SKTypeface face)
    {
        if (!_shapers.TryGetValue(face, out var shaper))
        {
            shaper = new SKShaper(face);
            _shapers[face] = shaper;
        }
        return shaper;
    }
}
