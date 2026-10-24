using Weighbridge.Core.Common;
using Weighbridge.Core.Indicators.Framing;

namespace Weighbridge.Core.Indicators.Learning;

public sealed record FramingSuggestion(FramingOptions Framing, double Confidence, string Explanation);

/// <summary>
/// Looks at a raw byte capture and proposes how the stream is cut into messages (setup wizard step 6).
/// </summary>
public static class FramingAnalyzer
{
    private const byte Stx = 0x02, Etx = 0x03, Cr = 0x0D, Lf = 0x0A;

    public static IReadOnlyList<FramingSuggestion> Analyze(ReadOnlySpan<byte> stream)
    {
        var suggestions = new List<FramingSuggestion>();
        if (stream.Length < 8) return suggestions;

        var stx = Positions(stream, Stx);
        var cr = Positions(stream, Cr);
        var lf = Positions(stream, Lf);
        var etx = Positions(stream, Etx);
        var crlf = 0;
        for (var i = 0; i + 1 < stream.Length; i++)
        {
            if (stream[i] == Cr && stream[i + 1] == Lf) crlf++;
        }

        if (stx.Count >= 3) AnalyzeStartEnd(stream, stx, suggestions);

        if (cr.Count + lf.Count >= 3)
        {
            string terminator;
            string explanation;
            if (crlf >= 3 && crlf >= 0.8 * Math.Max(cr.Count, lf.Count))
            {
                terminator = "\\r\\n";
                explanation = $"Messages end with CR LF ({crlf} seen).";
            }
            else if (cr.Count >= lf.Count)
            {
                terminator = "\\r";
                explanation = $"Messages end with CR ({cr.Count} seen).";
            }
            else
            {
                terminator = "\\n";
                explanation = $"Messages end with LF ({lf.Count} seen).";
            }
            var boundaries = cr.Count >= lf.Count ? cr : lf;
            var regularity = Regularity(boundaries);
            var confidence = 0.55 + 0.4 * regularity;
            // When STX/ETX framing was also found, CR inside STX frames is part of that protocol.
            if (suggestions.Count > 0) confidence -= 0.25;
            suggestions.Add(new FramingSuggestion(new FramingOptions { Mode = FramingMode.Terminator, Terminator = terminator, AnyLineEnding = true }, confidence, explanation));
        }

        if (etx.Count >= 3 && stx.Count < 3)
        {
            suggestions.Add(new FramingSuggestion(new FramingOptions { Mode = FramingMode.Terminator, Terminator = "<ETX>", AnyLineEnding = false },
                0.5 + 0.4 * Regularity(etx), $"Messages end with ETX ({etx.Count} seen)."));
        }

        var fixedSuggestion = AnalyzeFixedLength(stream);
        if (fixedSuggestion is not null) suggestions.Add(fixedSuggestion);

        if (suggestions.Count == 0)
        {
            suggestions.Add(new FramingSuggestion(new FramingOptions { Mode = FramingMode.IdleGap, IdleGapMs = 50 }, 0.2,
                "No delimiter found. Messages will be separated by pauses in transmission (idle gap)."));
        }

        return suggestions.OrderByDescending(s => s.Confidence).ToList();
    }

    private static void AnalyzeStartEnd(ReadOnlySpan<byte> stream, List<int> stx, List<FramingSuggestion> suggestions)
    {
        var endCounts = new Dictionary<byte, int>();
        var trailerCounts = new Dictionary<int, int>();
        var frames = 0;
        for (var k = 0; k + 1 < stx.Count; k++)
        {
            var from = stx[k] + 1;
            var to = stx[k + 1];
            var endPos = -1;
            byte endByte = 0;
            for (var i = from; i < to; i++)
            {
                if (stream[i] is Cr or Etx)
                {
                    endPos = i;
                    endByte = stream[i];
                    break;
                }
            }
            if (endPos < 0) continue;
            frames++;
            endCounts[endByte] = endCounts.GetValueOrDefault(endByte) + 1;
            var gap = to - endPos - 1;
            trailerCounts[gap] = trailerCounts.GetValueOrDefault(gap) + 1;
        }
        if (frames < 2) return;

        var (end, endHits) = endCounts.MaxBy(e => e.Value);
        var (trailer, trailerHits) = trailerCounts.MaxBy(t => t.Value);
        var consistency = Math.Min(endHits, trailerHits) / (double)frames;
        if (trailer > 4) trailer = 0;

        var endName = end == Cr ? "<CR>" : "<ETX>";
        var options = new FramingOptions
        {
            Mode = FramingMode.StartEnd,
            StartMarker = "<STX>",
            EndMarker = endName,
            TrailerLength = trailer,
            MaxFrameLength = 128,
        };
        var explanation = $"Messages start with STX and end with {endName.Trim('<', '>')}" +
                          (trailer > 0 ? $", followed by {trailer} trailer byte(s) (probably a checksum)." : ".");
        suggestions.Add(new FramingSuggestion(options, 0.6 + 0.38 * consistency, explanation));
    }

    private static FramingSuggestion? AnalyzeFixedLength(ReadOnlySpan<byte> stream)
    {
        if (stream.Length < 64) return null;
        var best = 0;
        var bestScore = 0.0;
        for (var length = 4; length <= 64 && length * 3 < stream.Length; length++)
        {
            int compared = 0, equal = 0;
            for (var i = 0; i + length < stream.Length; i++)
            {
                if (char.IsAsciiDigit((char)stream[i])) continue; // digits change with the weight
                compared++;
                if (stream[i] == stream[i + length]) equal++;
            }
            if (compared < 8) continue;
            var score = equal / (double)compared;
            if (score > bestScore + 0.02)
            {
                bestScore = score;
                best = length;
            }
        }
        if (best == 0 || bestScore < 0.9) return null;

        // Pick a non-digit byte that sits at the same position in every period as the sync marker.
        byte? sync = null;
        for (var offset = 0; offset < best && sync is null; offset++)
        {
            var b = stream[offset];
            if (char.IsAsciiDigit((char)b) || b == (byte)' ') continue;
            var constant = true;
            for (var i = offset; i < stream.Length; i += best)
            {
                if (stream[i] != b) { constant = false; break; }
            }
            if (constant && CountOf(stream, b) * best <= stream.Length + best) sync = b;
        }

        var options = new FramingOptions
        {
            Mode = FramingMode.FixedLength,
            FixedLength = best,
            StartMarker = sync is { } s ? ByteText.ToNotation([s]) : null,
        };
        return new FramingSuggestion(options, 0.45 + 0.3 * bestScore,
            $"The stream repeats every {best} bytes" + (sync is { } m ? $", synchronised on '{ByteText.ToDisplay([m])}'." : "."));
    }

    private static List<int> Positions(ReadOnlySpan<byte> data, byte value)
    {
        var list = new List<int>();
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == value) list.Add(i);
        }
        return list;
    }

    private static int CountOf(ReadOnlySpan<byte> data, byte value)
    {
        var n = 0;
        foreach (var b in data)
        {
            if (b == value) n++;
        }
        return n;
    }

    /// <summary>1 when the distances between boundaries are all equal, approaching 0 when they vary widely.</summary>
    private static double Regularity(List<int> positions)
    {
        if (positions.Count < 3) return 0.3;
        var gaps = new List<int>();
        for (var i = 1; i < positions.Count; i++) gaps.Add(positions[i] - positions[i - 1]);
        var mean = gaps.Average();
        if (mean <= 0) return 0;
        var sd = Math.Sqrt(gaps.Sum(g => (g - mean) * (g - mean)) / gaps.Count);
        return Math.Clamp(1 - sd / mean, 0, 1);
    }
}
