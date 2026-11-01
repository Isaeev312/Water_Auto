// Консоль для расчёта по проекту и для тестов.
//   calc <проект.waproj> --out <папка> [--data <папка data>]  — сквозной расчёт: план, аксонометрия, спецификация, записка
//   new <проект.waproj> --drawing <чертёж.dwg|dxf>              — заготовка файла проекта с исходными данными
//   template <файл.dwt|dwg|dxf>                                 — шаблон чертежа: слои ПТ_, мм, блок ввода, памятка
//   check <проект.waproj> [--data <папка data>]                 — проверка чертежа без расчёта: помещения, вводы, замечания с координатами
//   pdf <папка выпуска>                                          — docx выпуска в PDF через Word с обновлённым оглавлением (Windows)
using System.Text;
using WaterAuto.Application;

Console.OutputEncoding = Encoding.UTF8;
const string Usage = """
    Использование:
      WaterAuto.Cli calc <проект.waproj> --out <папка> [--data <папка data>]
      WaterAuto.Cli new <проект.waproj> --drawing <чертёж.dwg|dxf>
      WaterAuto.Cli template <файл.dwt|dwg|dxf>
      WaterAuto.Cli check <проект.waproj> [--data <папка data>]
      WaterAuto.Cli pdf <папка выпуска>
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

    case "check":
        {
            var project = Path.GetFullPath(args[1]);
            var root = Option("--data") is { } data ? Path.GetDirectoryName(Path.GetFullPath(data).TrimEnd(Path.DirectorySeparatorChar))! : DataLocator.FindRoot(Path.GetDirectoryName(project)!, AppContext.BaseDirectory);
            var preview = DrawingPreview.Analyze(project, root is null ? null : DataLocator.Norms(root));
            var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
            if (!preview.DrawingFound)
            {
                Console.Error.WriteLine($"Чертёж не найден: {preview.DrawingPath}");
                return 2;
            }

            Console.WriteLine($"Чертёж: {preview.DrawingPath}");
            Console.WriteLine(string.Create(ru, $"Помещений: {preview.Rooms.Count}, площадь {preview.TotalProtectedAreaM2:0.0} м²; точек ПТ_ВВОД: {preview.Inlets}; колонн {preview.Columns}, балок {preview.Beams}, коробов {preview.Ducts}, зон без оросителей {preview.NoSprinklerZones}."));
            foreach (var room in preview.Rooms)
            {
                Console.WriteLine(string.Create(ru, $"  {(room.Name.Length > 0 ? room.Name : "(без имени)")} — {room.AreaM2:0.0} м²"));
            }

            foreach (var s in preview.Suggestions)
            {
                Console.WriteLine(string.Create(ru, $"Предложение: {s.Label} = {s.Value:0.###} {s.Unit} ({s.Basis})"));
            }

            foreach (var issue in preview.Issues.OrderByDescending(i => i.Level))
            {
                var at = issue.Location is { } p ? string.Create(ru, $" — X {p.XMm:0}, Y {p.YMm:0} мм") : "";
                Console.WriteLine($"[{WaterAuto.Core.IssueLevels.Ru(issue.Level)}] {issue.Message}{at}");
            }

            Console.WriteLine(preview.HasErrors ? "Чертёж к расчёту не готов." : "Чертёж готов к расчёту.");
            return preview.HasErrors ? 1 : 0;
        }

    case "pdf":
        {
            var folder = Path.GetFullPath(args[1]);
            if (!Directory.Exists(folder))
            {
                Console.Error.WriteLine($"Папка не найдена: {folder}");
                return 2;
            }

            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("Выгрузка в PDF через Word доступна только в Windows.");
                return 2;
            }

            return WordPdf.Export(folder);
        }

    case "template":
        {
            var path = Path.GetFullPath(args[1]);
            DrawingTemplate.Write(path);
            Console.WriteLine($"Шаблон: {path}. Архитектурный план — внешней ссылкой в 0,0, контуры и препятствия — на слоях ПТ_ (памятка в файле).");
            return 0;
        }

    default:
        Console.Error.WriteLine($"Неизвестная команда «{args[0]}».");
        Console.Error.WriteLine(Usage);
        return 2;
}
