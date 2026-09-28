using System.Reflection;

namespace Shop.Common;

/// <summary>
/// Дефект, вшитый в эту сборку (см. Directory.Build.props). Используется
/// только стендом: инжектор собирает «плохую» версию образа, чтобы
/// сымитировать неудачный деплой.
/// </summary>
public static class BuildFault
{
    public static string Current { get; } =
        Assembly.GetEntryAssembly()?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ShopBuildFault")?.Value ?? "none";

    public static bool Is(string fault) => string.Equals(Current, fault, StringComparison.Ordinal);
}
