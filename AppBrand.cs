using System.Reflection;

namespace Tractus.Ndi.ConfigTui;

internal static class AppBrand
{
    public const string Title = "Config Editor for NDI | Tractus Events";

    public static string Version { get; } = GetVersion();

    public static string DisplayVersion => $"v{Version}";

    private static string GetVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var metadataIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
            return metadataIndex < 0
                ? informationalVersion
                : informationalVersion[..metadataIndex];
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
