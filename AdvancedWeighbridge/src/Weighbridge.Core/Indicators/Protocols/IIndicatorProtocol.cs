using System.Diagnostics.CodeAnalysis;
using Weighbridge.Core.Indicators.Framing;
using Weighbridge.Core.Indicators.Parsing;

namespace Weighbridge.Core.Indicators.Protocols;

/// <summary>
/// Contract for indicator protocols that cannot be described by the configurable text parser (binary status
/// bytes, proprietary encodings). Implementations live in this assembly or in plug-in assemblies placed in the
/// application's <c>plugins</c> folder; the main application never needs to change to support them.
/// </summary>
public interface IIndicatorProtocol
{
    /// <summary>Stable identifier stored in device profiles, e.g. <c>toledo-continuous</c>.</summary>
    string Name { get; }

    string DisplayName { get; }

    string Description { get; }

    /// <summary>Framing the setup wizard applies when the protocol is selected.</summary>
    FramingOptions RecommendedFraming { get; }

    ChecksumOptions RecommendedChecksum { get; }

    /// <summary>Optional request the indicator needs before it answers (poll mode), in profile notation. Null = continuous output.</summary>
    string? PollCommand { get; }

    ParseResult Parse(RawFrame frame, ParserOptions options);
}

public sealed class ProtocolRegistry
{
    private readonly Dictionary<string, IIndicatorProtocol> _protocols = new(StringComparer.OrdinalIgnoreCase);

    public static ProtocolRegistry Default { get; } = CreateDefault();

    public static ProtocolRegistry CreateDefault()
    {
        var registry = new ProtocolRegistry();
        registry.Register(new ToledoContinuousProtocol());
        return registry;
    }

    public void Register(IIndicatorProtocol protocol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol.Name);
        _protocols[protocol.Name] = protocol;
    }

    public bool TryGet(string name, [NotNullWhen(true)] out IIndicatorProtocol? protocol) => _protocols.TryGetValue(name, out protocol);

    public IReadOnlyCollection<IIndicatorProtocol> All => _protocols.Values;
}
