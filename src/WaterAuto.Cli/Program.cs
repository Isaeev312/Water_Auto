// Консоль для тестов и пакетного расчёта. На этапе 0 команд расчёта ещё нет.
const string Usage = "Использование: WaterAuto.Cli calc <проект.waproj> --out <папка>";

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(Usage);
    return 0;
}

Console.Error.WriteLine($"Команда «{args[0]}» пока не реализована (этап 0).");
Console.Error.WriteLine(Usage);
return 2;
