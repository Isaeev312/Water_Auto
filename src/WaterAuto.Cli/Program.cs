// Консоль для расчёта по проекту и для тестов.
//   calc <проект.waproj> --out <папка> [--data <папка data>]  — сквозной расчёт: план, аксонометрия, спецификация, записка
//   new <проект.waproj> --drawing <чертёж.dwg|dxf>              — заготовка файла проекта с исходными данными
using System.Text;
using WaterAuto.Application;

Console.OutputEncoding = Encoding.UTF8;
const string Usage = """
    Использование:
      WaterAuto.Cli calc <проект.waproj> --out <папка> [--data <папка data>]
      WaterAuto.Cli new <проект.waproj> --drawing <чертёж.dwg|dxf>
    """;

if (args.Length < 2 || args[0] is "-h" or "--help")
{
    Console.WriteLine(Usage);
    return args.Length == 0 || args[0] is "-h" or "--help" ? 0 : 2;
}

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

switch (args[0])
{
    case "calc":
    {
        var project = Path.GetFullPath(args[1]);
        var output = Option("--out") ?? Path.Combine(Path.GetDirectoryName(project)!, "out");
        var root = Option("--data") is { } data ? Path.GetDirectoryName(Path.GetFullPath(data).TrimEnd(Path.DirectorySeparatorChar))! : DataLocator.FindRoot(Path.GetDirectoryName(project)!, AppContext.BaseDirectory);
        if (root is null)
        {
            Console.Error.WriteLine("Не найдена папка data/norms: укажите --data <путь к папке data>.");
            return 2;
        }

        var result = ProjectPipeline.Run(project, output, DataLocator.Norms(root), DataLocator.Registry(root));
        foreach (var level in new[] { WaterAuto.Core.IssueLevel.Error, WaterAuto.Core.IssueLevel.Warning })
        {
            var count = result.Issues.Count(i => i.Level == level);
            if (count > 0)
            {
                Console.WriteLine($"{(level == WaterAuto.Core.IssueLevel.Error ? "Ошибок" : "Предупреждений")}: {count}");
            }
        }

        if (result.Design is { } d)
        {
            Console.WriteLine($"Расход насоса {d.PumpFlow}, напор {d.PumpHead}, запас воды {d.WaterVolume:0.0} м³.");
        }

        Console.WriteLine(result.Success ? "Готово:" : "Расчёт остановлен, замечания:");
        foreach (var file in result.Files)
        {
            Console.WriteLine($"  {file}");
        }

        return result.Success ? 0 : 1;
    }

    case "new":
    {
        var path = Path.GetFullPath(args[1]);
        if (File.Exists(path))
        {
            Console.Error.WriteLine($"Файл уже есть: {path}");
            return 2;
        }

        var drawing = Option("--drawing") ?? "plan.dwg";
        var relative = Path.GetRelativePath(Path.GetDirectoryName(path)!, Path.GetFullPath(drawing));
        File.WriteAllText(path, ProjectFile.ToJson(new ProjectFile { Name = Path.GetFileNameWithoutExtension(path), Drawing = relative }), new UTF8Encoding(false));
        Console.WriteLine($"Создан {path}. Заполните design: sprinklerK (по паспорту оросителя), dictatingAboveAxis, supplyLength, supplyDn, inletPressure (по ТУ), designVelocity.");
        return 0;
    }

    default:
        Console.Error.WriteLine($"Неизвестная команда «{args[0]}».");
        Console.Error.WriteLine(Usage);
        return 2;
}
