using System.Reflection;

namespace CS2Manager;

public static class AppInfo
{
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        string? version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(version)) return "6.3.0";
        int plus = version.IndexOf('+');
        return plus > 0 ? version[..plus] : version;
    }
}
