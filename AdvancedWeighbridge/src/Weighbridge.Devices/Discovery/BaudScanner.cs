using Weighbridge.Core.Indicators;
using Weighbridge.Core.Indicators.Learning;
using Weighbridge.Devices.Transports;

namespace Weighbridge.Devices.Discovery;

/// <summary>
/// Setup wizard step 3: listens on a port with each candidate baud rate / data format and ranks them by how
/// clean the received data looks. Only works for indicators that transmit continuously; poll-mode indicators
/// need the request command configured first (the scanner sends it when given).
/// </summary>
public sealed class BaudScanner(Func<SerialSettings, IByteTransport>? transportFactory = null)
{
    private readonly Func<SerialSettings, IByteTransport> _factory = transportFactory ?? (s => new SerialByteTransport(s));

    public async Task<IReadOnlyList<LineScore>> ScanAsync(
        string portName,
        IEnumerable<LineSettingsCandidate>? candidates = null,
        int listenMs = 1200,
        byte[]? pollCommand = null,
        IProgress<LineScore>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var scores = new List<LineScore>();
        foreach (var candidate in candidates ?? LineQualityScorer.DefaultScanOrder())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settings = new SerialSettings
            {
                PortName = portName,
                BaudRate = candidate.BaudRate,
                DataBits = candidate.DataBits,
                Parity = candidate.Parity,
                StopBits = candidate.StopBits,
            };
            var score = await Task.Run(() => Listen(settings, candidate, listenMs, pollCommand), cancellationToken).ConfigureAwait(false);
            scores.Add(score);
            progress?.Report(score);
            if (score.Score >= 0.97) break; // unmistakably right; no need to try the rest
        }
        return scores.OrderByDescending(s => s.Score).ToList();
    }

    private LineScore Listen(SerialSettings settings, LineSettingsCandidate candidate, int listenMs, byte[]? poll)
    {
        using var transport = _factory(settings);
        try
        {
            transport.Open();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return new LineScore(candidate, 0, 0, 0, false, 0);
        }

        var received = new List<byte>(4096);
        var buffer = new byte[1024];
        var deadline = Environment.TickCount64 + listenMs;
        var nextPoll = 0L;
        try
        {
            while (Environment.TickCount64 < deadline && received.Count < 8192)
            {
                if (poll is { Length: > 0 } && Environment.TickCount64 >= nextPoll)
                {
                    transport.Write(poll);
                    nextPoll = Environment.TickCount64 + 200;
                }
                var n = transport.Read(buffer, 50);
                for (var i = 0; i < n; i++) received.Add(buffer[i]);
            }
        }
        catch (IOException)
        {
            // Treat as no data for this setting.
        }
        var errors = transport is SerialByteTransport s ? s.TakeUartErrors() : 0;
        return LineQualityScorer.Score(candidate, received.ToArray(), errors);
    }
}
