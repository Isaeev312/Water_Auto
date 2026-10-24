using System.Xml.Linq;

namespace WaterAuto.Architecture.Tests;

// Правило зависимостей (ARCHITECTURE.md, раздел 3): ссылки направлены только к ядру.
// Проверяются ссылки между проектами в .csproj, а не ссылки сборок: компилятор
// выбрасывает ссылку на сборку, из которой не используется ни один тип, поэтому
// на пустом скелете проверка сборок ничего бы не ловила.
public class ProjectReferenceTests
{
    private static readonly string[] Libraries = ["Calc", "Norms", "Catalog", "Import", "Export", "Reports"];

    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["WaterAuto.Core"] = [],
        ["WaterAuto.Calc"] = ["WaterAuto.Core"],
        ["WaterAuto.Norms"] = ["WaterAuto.Core"],
        ["WaterAuto.Catalog"] = ["WaterAuto.Core"],
        ["WaterAuto.Import"] = ["WaterAuto.Core"],
        ["WaterAuto.Export"] = ["WaterAuto.Core"],
        ["WaterAuto.Reports"] = ["WaterAuto.Core"],
        ["WaterAuto.Application"] = ["WaterAuto.Core", .. Libraries.Select(n => $"WaterAuto.{n}")],
        ["WaterAuto.Cli"] = ["WaterAuto.Core", "WaterAuto.Application"],
        ["WaterAuto.App"] = ["WaterAuto.Core", "WaterAuto.Application"],
    };

    public static TheoryData<string> SourceProjects()
    {
        var data = new TheoryData<string>();
        foreach (var name in ProjectNames())
        {
            data.Add(name);
        }

        return data;
    }

    [Fact]
    public void Every_source_project_has_a_dependency_rule()
    {
        ProjectNames().Where(n => !Allowed.ContainsKey(n)).ShouldBeEmpty(
            "Новый проект в src/ — добавьте его в таблицу Allowed и в ARCHITECTURE.md.");
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void Project_references_only_allowed_projects(string project)
    {
        var allowed = Allowed.GetValueOrDefault(project, []);
        References(project).Where(r => !allowed.Contains(r)).ShouldBeEmpty(
            $"{project} может ссылаться только на: {(allowed.Length == 0 ? "ничего" : string.Join(", ", allowed))}.");
    }

    [Fact]
    public void Calc_does_not_reference_io_or_ui()
    {
        string[] forbidden = ["WaterAuto.Import", "WaterAuto.Export", "WaterAuto.Reports", "WaterAuto.App"];
        References("WaterAuto.Calc").Intersect(forbidden).ShouldBeEmpty();
    }

    private static string SrcDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WaterAuto.sln")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? throw new InvalidOperationException("Не найден корень репозитория (WaterAuto.sln).")
            : Path.Combine(dir.FullName, "src");
    }

    private static IEnumerable<string> ProjectNames() =>
        Directory.EnumerateFiles(SrcDir(), "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order(StringComparer.Ordinal);

    private static string[] References(string project)
    {
        var path = Path.Combine(SrcDir(), project, project + ".csproj");
        return XDocument.Load(path)
            .Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include") ?? "")
            .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
            .ToArray();
    }
}
