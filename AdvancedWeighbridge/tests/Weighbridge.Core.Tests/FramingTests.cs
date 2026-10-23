using System.Text;
using Weighbridge.Core.Common;
using Weighbridge.Core.Indicators.Framing;

namespace Weighbridge.Core.Tests;

public class FramingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    private static byte[] B(string s) => Encoding.Latin1.GetBytes(s);
    private static string S(byte[] b) => Encoding.Latin1.GetString(b);

    [Fact]
    public void Terminator_reassembles_fragmented_frames()
    {
        var a = new FrameAssembler(new FramingOptions());
        Assert.Empty(a.Feed(B("ST,GS,+0012"), T0));
        Assert.Empty(a.Feed(B("34.5k"), T0));
        var frames = a.Feed(B("g\r\n"), T0);
        Assert.Single(frames);
        Assert.Equal("ST,GS,+001234.5kg", S(frames[0].Payload));
    }

    [Fact]
    public void Terminator_splits_concatenated_frames_and_accepts_any_line_ending()
    {
        var a = new FrameAssembler(new FramingOptions());
        var frames = a.Feed(B("12345\r\n12346\r12347\n\r\n12348"), T0);
        Assert.Equal(["12345", "12346", "12347"], frames.Select(f => S(f.Payload)));
        Assert.Equal(5, a.BufferedByteCount); // "12348" waits for its terminator
        Assert.Equal(3, frames[^1].Sequence);
    }

    [Fact]
    public void Custom_terminator_etx()
    {
        var a = new FrameAssembler(new FramingOptions { Terminator = "<ETX>" });
        var frames = a.Feed(B("\u0002 12500kg\u0003\u0002 12510kg\u0003"), T0);
        Assert.Equal(2, frames.Count);
        Assert.Equal("\u0002 12500kg", S(frames[0].Payload));
    }

    [Fact]
    public void Overflow_discards_buffer_and_reports_issue()
    {
        var a = new FrameAssembler(new FramingOptions { MaxFrameLength = 16 });
        Assert.Empty(a.Feed(B(new string('9', 40)), T0));
        var issues = a.DrainIssues();
        Assert.Contains(issues, i => i.Kind == FramingIssueKind.Overflow);
        var frames = a.Feed(B("\r\n100\r\n"), T0);
        Assert.Equal(["100"], frames.Select(f => S(f.Payload)).Where(p => p == "100"));
    }

    [Fact]
    public void StartEnd_with_checksum_trailer_and_resync()
    {
        var a = new FrameAssembler(new FramingOptions
        {
            Mode = FramingMode.StartEnd, StartMarker = "<STX>", EndMarker = "<CR>", TrailerLength = 1,
        });
        // Garbage, an interrupted frame, then a complete frame with a 1-byte trailer.
        var data = B("xx\u0002ABC\u0002DEF\rZ");
        var frames = a.Feed(data, T0);
        Assert.Single(frames);
        Assert.Equal("DEF", S(frames[0].Payload));
        Assert.Equal("Z", S(frames[0].Trailer));
        Assert.Equal("\u0002DEF\rZ", S(frames[0].Full));
        var issues = a.DrainIssues();
        Assert.Contains(issues, i => i.Kind == FramingIssueKind.BytesSkipped);
        Assert.Contains(issues, i => i.Kind == FramingIssueKind.IncompleteFrameDiscarded);
    }

    [Fact]
    public void StartEnd_frame_split_across_reads()
    {
        var a = new FrameAssembler(new FramingOptions { Mode = FramingMode.StartEnd, StartMarker = "<STX>", EndMarker = "<ETX>" });
        Assert.Empty(a.Feed(B("\u0002+00"), T0));
        Assert.Empty(a.Feed(B("1200"), T0));
        var frames = a.Feed(B("\u0003\u0002+001210\u0003"), T0);
        Assert.Equal(["+001200", "+001210"], frames.Select(f => S(f.Payload)));
    }

    [Fact]
    public void FixedLength_synchronises_on_start_marker()
    {
        var a = new FrameAssembler(new FramingOptions { Mode = FramingMode.FixedLength, FixedLength = 8, StartMarker = "=" });
        var frames = a.Feed(B("21=00.0210=00.0310=00"), T0);
        Assert.Equal(["00.0210", "00.0310"], frames.Select(f => S(f.Payload)));
    }

    [Fact]
    public void IdleGap_emits_on_silence()
    {
        var a = new FrameAssembler(new FramingOptions { Mode = FramingMode.IdleGap, IdleGapMs = 50 });
        Assert.Empty(a.Feed(B("12"), T0));
        Assert.Empty(a.Feed(B("500"), T0.AddMilliseconds(10)));
        Assert.Empty(a.Flush(T0.AddMilliseconds(30)));
        var flushed = a.Flush(T0.AddMilliseconds(70));
        Assert.Equal("12500", S(flushed.Single().Payload));

        // A new burst after a gap releases the previous one.
        a.Feed(B("12510"), T0.AddMilliseconds(200));
        var frames = a.Feed(B("12520"), T0.AddMilliseconds(400));
        Assert.Equal("12510", S(frames.Single().Payload));
    }

    [Theory]
    [InlineData("<STX>", new byte[] { 0x02 })]
    [InlineData("\\r\\n", new byte[] { 0x0D, 0x0A })]
    [InlineData("R\\r", new byte[] { 0x52, 0x0D })]
    [InlineData("\\x05", new byte[] { 0x05 })]
    [InlineData("<0x1B>P", new byte[] { 0x1B, 0x50 })]
    [InlineData("<ENQ><cr>", new byte[] { 0x05, 0x0D })]
    public void Unescape_profile_notation(string notation, byte[] expected)
    {
        Assert.Equal(expected, ByteText.Unescape(notation));
    }

    [Fact]
    public void Display_and_hex_round_trip()
    {
        var bytes = new byte[] { 0x02, 0x41, 0x0D, 0x0A, 0xFF };
        Assert.Equal("<STX>A<CR><LF><0xFF>", ByteText.ToDisplay(bytes));
        Assert.Equal(bytes, ByteText.Unescape(ByteText.ToNotation(bytes)));
        Assert.Equal("02 41 0D 0A FF", ByteText.ToHex(bytes));
        Assert.Equal(bytes, ByteText.FromHex("02-41-0d 0A:ff"));
        Assert.Contains("0000  02 41 0D 0A FF", ByteText.HexDump(bytes));
    }
}

public class ChecksumTests
{
    [Fact]
    public void Crc16_modbus_known_vector()
    {
        // Standard check value of CRC-16/MODBUS for "123456789" is 0x4B37.
        Assert.Equal(0x4B37, ChecksumValidator.Crc16Modbus(Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public void Twos_complement_seven_bit_sums_to_zero()
    {
        var data = Encoding.ASCII.GetBytes("\u0002 0 1234000000\r");
        var cs = ChecksumValidator.Compute(ChecksumKind.TwosComplement7, data)[0];
        var sum = data.Sum(b => b) + cs;
        Assert.Equal(0, sum & 0x7F);
    }

    [Fact]
    public void Trailer_checksum_validation_detects_corruption()
    {
        var framing = new FramingOptions { Mode = FramingMode.StartEnd, StartMarker = "<STX>", EndMarker = "<CR>", TrailerLength = 1 };
        var options = new ChecksumOptions { Kind = ChecksumKind.Xor8 };
        var body = Encoding.ASCII.GetBytes("\u0002+012500\r");
        var cs = ChecksumValidator.Compute(ChecksumKind.Xor8, body);
        var good = new FrameAssembler(framing).Feed([.. body, .. cs], DateTimeOffset.UtcNow).Single();
        Assert.True(ChecksumValidator.Validate(good, framing, options).Valid);

        var corrupt = body.ToArray();
        corrupt[3] = (byte)'9';
        var bad = new FrameAssembler(framing).Feed([.. corrupt, .. cs], DateTimeOffset.UtcNow).Single();
        Assert.False(ChecksumValidator.Validate(bad, framing, options).Valid);
    }

    [Fact]
    public void Ascii_hex_checksum_at_end_of_payload()
    {
        var framing = new FramingOptions { Mode = FramingMode.Terminator };
        var options = new ChecksumOptions { Kind = ChecksumKind.Sum8, Location = ChecksumLocation.EndOfPayload, AsciiHex = true, IncludeStartMarker = false };
        var payload = Encoding.ASCII.GetBytes("WT12500");
        var hex = Convert.ToHexString(ChecksumValidator.Compute(ChecksumKind.Sum8, payload));
        var frame = new FrameAssembler(framing).Feed(Encoding.ASCII.GetBytes("WT12500" + hex + "\r\n"), DateTimeOffset.UtcNow).Single();
        Assert.True(ChecksumValidator.Validate(frame, framing, options).Valid);
    }
}
