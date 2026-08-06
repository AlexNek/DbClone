using System.Reflection;

namespace DbClone.UI;

internal static class AppFolder
{
    public static string Name => Assembly.GetEntryAssembly()?.GetName()?.Name ?? "DbClone";
}
