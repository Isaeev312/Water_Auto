namespace WaterAuto.Application;

/// <summary>Корень репозитория или установки — папка, в которой есть data/norms.</summary>
public static class DataLocator
{
    /// <summary>Ищет вверх от каждой из начальных папок; null — не найдено.</summary>
    public static string? FindRoot(params string[] starts)
    {
        foreach (var start in starts)
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "data", "norms")))
                {
                    return dir.FullName;
                }
            }
        }

        return null;
    }

    public static string Norms(string root) => Path.Combine(root, "data", "norms");

    public static string Registry(string root) => Path.Combine(root, "docs", "literature_registry.csv");
}
