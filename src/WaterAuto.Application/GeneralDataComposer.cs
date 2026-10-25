using System.Globalization;
using System.Text.Json;
using WaterAuto.Calc.Hydraulics;
using WaterAuto.Calc.Layout;
using WaterAuto.Core;
using WaterAuto.Reports;

namespace WaterAuto.Application;

/// <summary>Исходные данные листа «Общие данные»: результаты расчёта и решения проекта.</summary>
public sealed record GeneralDataInputs(
    string ObjectName,
    IReadOnlyList<ProjectSheet> Sheets,
    FloorPlan Plan,
    SprinklerLayout Layout,
    DesignResult Design,
    MaterialTakeoff Takeoff,
    TemperatureChoice? Temperature,
    int HydrantCount,
    string PipeStandard,
    string WeldStandard,
    IReadOnlyDictionary<string, string> FittingStandards);

/// <summary>
/// Лист «Общие данные» (ГОСТ Р 21.101): ведомость чертежей — из проекта; ссылочные документы — нормы расчёта и все
/// обозначения из спецификации с наименованиями из data/catalog/documents.json; основные показатели — из расчёта;
/// общие указания — решения проекта со ссылками на пункты норм (числа — из данных норм).
/// </summary>
public static class GeneralDataComposer
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string DocumentsPath(string normsDir) => Path.Combine(Path.GetDirectoryName(SprinklerCatalog.PathFor(normsDir))!, "documents.json");

    public static GeneralData Compose(GeneralDataInputs i, INormsProvider norms, string? documentsPath)
    {
        ArgumentNullException.ThrowIfNull(i);
        ArgumentNullException.ThrowIfNull(norms);
        var names = LoadNames(documentsPath);
        var d = i.Design;
        var p = d.Parameters;

        // Ссылочные документы: нормы расчёта, затем обозначения из спецификации, без повторов.
        var designations = new List<string> { "СП 485.1311500.2020" };
        if (d.Hydrants is { } h)
        {
            designations.Add("СН КР 40-04:2025");
            if (h.Settings.IsParking)
            {
                designations.Add("СНиП 21-02-99*");
            }
        }

        designations.AddRange(["СП 7.13130.2013", "СП 75.13330.2011", "ГОСТ Р 50680-94", "ГОСТ Р 51043-2002", "ГОСТ Р 51052-2002", i.WeldStandard]);
        designations.AddRange(i.Takeoff.Items.Select(t => t.Designation).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!));
        designations.AddRange(["ГОСТ 21.110-2013", "ГОСТ Р 21.101-2026"]);
        // Только обозначения документов (марки изделий из спецификации, например «СВВ-12», сюда не входят).
        string[] kinds = ["ГОСТ", "СП ", "СН ", "СНиП", "ТУ ", "DIN", "ISO", "РД ", "ОСТ"];
        var references = designations
            .Where(x => kinds.Any(k => x.StartsWith(k, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Select(x => new ReferenceRow(x, names.GetValueOrDefault(x, ""), names.ContainsKey(x) ? "" : "наименование уточнить"))
            .ToList();

        var area = i.Plan.Rooms.Sum(r => r.Contour.AreaMm2) / 1e6;
        var sprinklers = i.Takeoff.Items.Where(t => t.Group == TakeoffGroup.Sprinklers).ToList();
        var indicators = new List<IndicatorRow>
        {
            new("Площадь защищаемых помещений", "м²", F(area, "0.0")),
            new("Группа помещений по приложению А СП 485", "—", p.Group),
            new($"Интенсивность орошения ({p.Source})", "л/(с·м²)", F(p.Intensity, "0.###")),
            new("Минимальная площадь срабатывания", "м²", F(p.MinArea, "0")),
            new("Расчётный расход спринклерной установки", "л/с", F(d.DesignFlow.LitersPerSecond, "0.00")),
            new("Расход пожарного насоса", "л/с (м³/ч)", $"{F(d.PumpFlow.LitersPerSecond, "0.00")} ({F(d.PumpFlow.LitersPerSecond * 3.6, "0.0")})"),
            new("Требуемый напор пожарного насоса", "м", F(d.PumpHead.Meters, "0.0")),
            new("Продолжительность подачи воды", "мин", F(d.Duration, "0")),
            new("Запас воды на пожаротушение", "м³", F(d.WaterVolume, "0.0")),
            new("Количество оросителей (в том числе запас)", "шт.", $"{F(sprinklers.Sum(s => s.Quantity), "0")} ({F(sprinklers.Skip(1).Sum(s => s.Quantity), "0")})"),
        };
        if (i.Temperature is { } t)
        {
            indicators.Add(new("Номинальная температура срабатывания оросителей", "°С", F(t.Nominal, "0")));
        }

        if (d.Hydrants is { } hy)
        {
            indicators.Add(new("Внутренний противопожарный водопровод: число струй × расход струи", "шт. × л/с", $"{hy.Demand.Jets} × {F(hy.JetFlowActual.LitersPerSecond, "0.00")}"));
            if (i.HydrantCount > 0)
            {
                indicators.Add(new("Количество пожарных кранов", "шт.", i.HydrantCount.ToString(Ru)));
            }
        }

        var instructions = new List<string>
        {
            $"Рабочие чертежи автоматической установки водяного пожаротушения объекта «{i.ObjectName}» выполнены в соответствии с СП 485.1311500.2020"
                + (d.Hydrants is null ? "" : d.Hydrants.Settings.IsParking ? ", внутренний противопожарный водопровод — СН КР 40-04:2025 и СНиП 21-02-99*" : ", внутренний противопожарный водопровод — СН КР 40-04:2025")
                + ".",
            $"Установка — спринклерная водозаполненная. Параметры приняты для группы помещений {p.Group} по {p.Source} СП 485.",
            $"Трубопроводы — стальные по {i.PipeStandard}; соединения — на сварке по {i.WeldStandard}; фасонные части — {string.Join(", ", i.FittingStandards.Select(f => $"{Plural(f.Key)} по {f.Value}"))}.",
            "Монтаж трубопроводов и оборудования, их крепление — по СП 75.13330; приёмо-сдаточные испытания установки — по ГОСТ Р 50680.",
            "Технические средства установки, кроме оросителей, измерительных приборов и трубопроводов, окрасить в красный цвет (п. 6.8.11 СП 485).",
        };
        if (norms.Find("sp485.p6_7_2_10.earthing")?.Values["minPoints"] is { } points)
        {
            instructions.Add($"Стальные трубопроводы присоединить к контуру заземления не менее чем в {F(points, "0")} точках (п. 6.7.2.10 СП 485).");
        }

        instructions.Add("Размеры даны в миллиметрах, отметки — в метрах.");
        instructions.Add("Расчёт выполнен программой Water_Auto; результаты подлежат проверке специалистом по противопожарным системам.");

        return new GeneralData(
            $"{i.ObjectName}. Автоматическая установка водяного пожаротушения. Общие данные",
            i.Sheets.Select(s => new SheetRow(s.Sheet, s.Name, s.Note)).ToList(),
            references,
            indicators,
            instructions);
    }

    private static Dictionary<string, string> LoadNames(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("documents").EnumerateArray()
            .ToDictionary(x => x.GetProperty("designation").GetString()!, x => x.GetProperty("name").GetString()!, StringComparer.Ordinal);
    }

    private static string F(double v, string format) => v.ToString(format, Ru);

    // «Отвод» → «отводы», «Тройник» → «тройники».
    internal static string Plural(string noun)
    {
        var lower = noun.ToLower(Ru);
        return lower + ("кгхжшчщ".Contains(lower[^1], StringComparison.Ordinal) ? "и" : "ы");
    }
}
