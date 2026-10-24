using Weighbridge.Core.Indicators.Framing;
using Weighbridge.Core.Indicators.Parsing;
using Weighbridge.Core.Indicators.Protocols;

namespace Weighbridge.Core.Indicators;

/// <summary>One decoded frame with its outcome, as shown in the raw-data monitor.</summary>
public sealed record DecodedFrame(RawFrame Frame, ParseResult Result, ChecksumResult Checksum);

/// <summary>
/// Bytes → frames → checksum → parser for one profile. Pure and synchronous, so it is used identically by the live
/// indicator service, the parser test tool, sample replay and the unit tests.
/// </summary>
public sealed class IndicatorDecoder
{
    private readonly IndicatorProfile _profile;
    private readonly FrameAssembler _assembler;
    private readonly WeightParser _parser;

    public IndicatorDecoder(IndicatorProfile profile, ProtocolRegistry? protocols = null)
    {
        _profile = profile;
        _assembler = new FrameAssembler(profile.Framing);
        _parser = new WeightParser(profile.Parser, protocols);
    }

    public IndicatorProfile Profile => _profile;
    public FrameAssembler Assembler => _assembler;

    public IReadOnlyList<DecodedFrame> Feed(ReadOnlySpan<byte> data, DateTimeOffset receivedAt) => Decode(_assembler.Feed(data, receivedAt));

    public IReadOnlyList<DecodedFrame> Flush(DateTimeOffset now) => Decode(_assembler.Flush(now));

    public DecodedFrame DecodeFrame(RawFrame frame)
    {
        var checksum = ChecksumValidator.Validate(frame, _profile.Framing, _profile.Checksum);
        if (!checksum.Valid)
        {
            return new DecodedFrame(frame, ParseResult.Reject(frame, $"Checksum mismatch (expected {checksum.Expected}, received {checksum.Actual})."), checksum);
        }

        // When the checksum sits at the end of the payload, the parser must not see it.
        var toParse = frame;
        if (_profile.Checksum.Kind != ChecksumKind.None && _profile.Checksum.Location == ChecksumLocation.EndOfPayload)
        {
            var n = ChecksumValidator.ByteLength(_profile.Checksum.Kind) * (_profile.Checksum.AsciiHex ? 2 : 1);
            toParse = frame with { Payload = frame.Payload[..^n] };
        }
        return new DecodedFrame(frame, _parser.Parse(toParse), checksum);
    }

    private List<DecodedFrame> Decode(IReadOnlyList<RawFrame> frames)
    {
        var result = new List<DecodedFrame>(frames.Count);
        foreach (var f in frames) result.Add(DecodeFrame(f));
        return result;
    }
}
