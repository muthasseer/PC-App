using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Weighbridge.Core.Indicators.Protocols;

namespace Weighbridge.Devices.Plugins;

/// <summary>
/// Loads indicator protocol plug-ins (<see cref="IIndicatorProtocol"/> implementations) from the application's
/// <c>plugins</c> folder. The folder lives under Program Files and is only writable by administrators; plug-ins
/// are trusted code and are listed in the About / Devices screens.
/// </summary>
public static class PluginLoader
{
    public sealed record LoadedPlugin(string Assembly, string Protocol, string DisplayName);

    public static IReadOnlyList<LoadedPlugin> LoadProtocols(string directory, ProtocolRegistry registry, ILogger log)
    {
        var loaded = new List<LoadedPlugin>();
        if (!Directory.Exists(directory)) return loaded;

        foreach (var path in Directory.EnumerateFiles(directory, "*.dll"))
        {
            try
            {
                var context = new AssemblyLoadContext(Path.GetFileNameWithoutExtension(path), isCollectible: false);
                var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
                foreach (var type in SafeTypes(assembly))
                {
                    if (type.IsAbstract || !typeof(IIndicatorProtocol).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) is null) continue;
                    var protocol = (IIndicatorProtocol)Activator.CreateInstance(type)!;
                    registry.Register(protocol);
                    loaded.Add(new LoadedPlugin(Path.GetFileName(path), protocol.Name, protocol.DisplayName));
                    log.LogInformation("Loaded indicator protocol {Protocol} from {Assembly}", protocol.Name, Path.GetFileName(path));
                }
            }
            catch (Exception e) when (e is BadImageFormatException or FileLoadException or ReflectionTypeLoadException or TargetInvocationException or MissingMethodException)
            {
                log.LogWarning("Plug-in {Path} could not be loaded: {Message}", path, e.Message);
            }
        }
        return loaded;
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t is not null)!;
        }
    }
}
