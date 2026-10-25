namespace WaterAuto.Application.Tests;

internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string Norms => Path.Combine(Root, "data", "norms");

    public static string Registry => Path.Combine(Root, "docs", "literature_registry.csv");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WaterAuto.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Не найден корень репозитория (WaterAuto.sln).");
    }
}
