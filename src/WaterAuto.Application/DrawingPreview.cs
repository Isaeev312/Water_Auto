using System.Globalization;
using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;
using WaterAuto.Import;

namespace WaterAuto.Application;

/// <summary>Помещение, распознанное на чертеже: имя и площадь, м².</summary>
public sealed record DetectedRoom(string Id, string Name, double AreaM2);

/// <summary>
/// Предлагаемое значение исходных данных, вычисленное по чертежу и каталогу. <see cref="Key"/> — поле проекта
/// (<c>design.*</c>), <see cref="Basis"/> — на чём основан расчёт: пользователь видит, что проверять.
/// </summary>
public sealed record DrawingSuggestion(string Key, string Label, double Value, string Unit, string Basis, string Source);

/// <summary>
/// Результат проверки чертежа до расчёта: что распознано на плане и какие исходные данные можно заполнить автоматически.
/// Числа норм здесь не появляются — только геометрия чертежа, данные проекта и каталог установок.
/// </summary>
public sealed record DrawingPreviewResult(
    bool DrawingFound,
    string DrawingPath,
    IReadOnlyList<DetectedRoom> Rooms,
    double TotalProtectedAreaM2,
    int Columns,
    int Beams,
    int Ducts,
    int NoSprinklerZones,
    string? PumpRoomId,
    double? PumpRoomLengthM,
    double? PumpRoomWidthM,
    double? PumpRoomAreaM2,
    int PumpRoomInlets,
    int PumpRoomOutlets,
    int TruckInlets,
    bool PumpRoomHasDoor,
    int Inlets,
    int BuildingInlets,
    double? SlabBottomM,
    double? DuctBottomM,
    IReadOnlyList<DrawingSuggestion> Suggestions,
    IReadOnlyList<Issue> Issues)
{
    /// <summary>Ошибки чтения и соглашения о слоях — чертёж к расчёту не готов.</summary>
    public bool HasErrors => Issues.Any(i => i.Level == IssueLevel.Error);

    /// <summary>Замечания, которые не останавливают расчёт (предупреждения и информация).</summary>
    public int Warnings => Issues.Count(i => i.Level == IssueLevel.Warning);
}

/// <summary>
/// Проверка чертежа до расчёта и автоматическое заполнение исходных данных, которые выводятся из геометрии плана:
/// высота помещения и отметка труб — по низу перекрытия (слой ПТ_БАЛКИ), H и подводящий трубопровод — по отметкам
/// и расстояниям на плане. Всё, что предлагается, помечено основой расчёта и требует подтверждения пользователя.
/// </summary>
public static class DrawingPreview
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Чертёж из файла проекта; исключения чтения проекта не перехватываются — их обрабатывает вызывающий.</summary>
    public static DrawingPreviewResult Analyze(string projectPath, string? normsDir = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectPath);
        var project = ProjectFile.Load(projectPath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var drawing = project.Drawing.Length == 0 ? "" : Path.GetFullPath(Path.Combine(dir, project.Drawing));
        return Analyze(project, drawing, normsDir);
    }

    /// <summary>Разбор конкретного чертежа с данными проекта (для окна ввода и тестов).</summary>
    public static DrawingPreviewResult Analyze(ProjectFile project, string drawingPath, string? normsDir = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (drawingPath.Length == 0 || !File.Exists(drawingPath))
        {
            return Result(
                drawingFound: false,
                drawingPath,
                rooms: [],
                totalArea: 0,
                plan: null,
                importIssues: [new Issue("preview.drawingMissing", IssueLevel.Warning, $"Чертёж не найден: {drawingPath}. Проверьте путь в поле «Чертёж плана».")],
                project,
                normsDir);
        }

        ImportResult import;
        try
        {
            import = FloorPlanImporter.Import(drawingPath);
        }
#pragma warning disable CA1031 // окно проверки не должно падать на любом сбое чтения DWG/DXF
        catch (Exception ex)
        {
            import = new ImportResult(null, [new Issue("preview.read", IssueLevel.Error, $"Чертёж не прочитан ({ex.GetType().Name}): {ex.Message}")]);
        }
#pragma warning restore CA1031

        var plan = import.Plan;
        var rooms = plan is null
            ? new List<DetectedRoom>()
            : [.. plan.Rooms.Select(r => new DetectedRoom(r.Id, r.Name, Math.Round(r.Contour.AreaMm2 / 1_000_000, 2)))];
        var issues = import.Issues.ToList();
        if (plan is not null)
        {
            issues.AddRange(Preflight(plan));
        }

        return Result(
            drawingFound: true,
            drawingPath,
            [.. rooms],
            rooms.Sum(r => r.AreaM2),
            plan,
            issues,
            project,
            normsDir);
    }

    // Проверки подготовки чертежа: то, из-за чего выпуск насосной или расчёт будут неполными (README §7).
    private static IEnumerable<Issue> Preflight(FloorPlan plan)
    {
        if (plan.Rooms.Count == 0)
        {
            yield return new Issue("preview.noRooms", IssueLevel.Error, "На слое ПТ_КОНТУР нет ни одного помещения — расчёт не сможет построить раскладку.");
        }

        if (plan.PumpRooms.Count == 0)
        {
            yield return new Issue("preview.noPumpRoom", IssueLevel.Warning, "Помещение насосной (слой ПТ_НС) не найдено: листы насосной и ПТ2.СО не выпускаются.");
        }
        else
        {
            var room = plan.PumpRooms[0];
            if (room.Inlets.Count == 0)
            {
                yield return new Issue("preview.noStationInlet", IssueLevel.Warning, "В насосной нет вводов на слое ПТ_НС_ВВОД: программа поставит условные точки на стенах (проверьте п. 6.10.27).", room.Id);
            }

            if (room.Outlets.Count == 0)
            {
                yield return new Issue("preview.noStationOutlet", IssueLevel.Warning, "В насосной нет выпусков на слое ПТ_НС_ВЫПУСК: программа поставит условные точки на стенах (проверьте п. 6.10.28).", room.Id);
            }

            if (room.TruckInlets.Count == 0)
            {
                yield return new Issue("preview.noTruckInlets", IssueLevel.Warning, "Патрубки для мобильной техники (слой ПТ_НС_ГМ) не заданы: на схеме их не будет (п. 6.10.17).", room.Id);
            }
        }

        if (plan.Inlets.Count == 0)
        {
            yield return new Issue("preview.noInlets", IssueLevel.Warning, "Нет точек ПТ_ВВОД — некуда подключить сеть (README §7, таблица слоёв).");
        }
    }

    private static DrawingPreviewResult Result(
        bool drawingFound,
        string drawingPath,
        IReadOnlyList<DetectedRoom> rooms,
        double totalArea,
        FloorPlan? plan,
        IReadOnlyList<Issue> importIssues,
        ProjectFile project,
        string? normsDir)
    {
        var pumpRoom = plan is { PumpRooms.Count: > 0 } ? plan.PumpRooms[0] : null;
        var pumpArea = pumpRoom is null ? (double?)null : Math.Round(pumpRoom.Outline.AreaMm2 / 1_000_000, 2);
        (double? length, double? width) = pumpRoom is null ? (null, null) : BoundingBox(pumpRoom.Outline);
        var slabBottom = plan is { Beams.Count: > 0 } ? plan.Beams.Min(b => b.SlabBottom.Meters) : (double?)null;
        var ductBottom = plan is { Ducts.Count: > 0 } ? plan.Ducts.Min(d => d.Bottom.Meters) : (double?)null;
        var suggestions = Suggestions(project, plan, slabBottom, pumpRoom, normsDir);
        return new DrawingPreviewResult(
            drawingFound,
            drawingPath,
            rooms,
            Math.Round(totalArea, 1),
            plan?.Columns.Count ?? 0,
            plan?.Beams.Count ?? 0,
            plan?.Ducts.Count ?? 0,
            plan?.NoSprinklerZones.Count ?? 0,
            pumpRoom?.Id,
            length,
            width,
            pumpArea,
            pumpRoom?.Inlets.Count ?? 0,
            pumpRoom?.Outlets.Count ?? 0,
            pumpRoom?.TruckInlets.Count ?? 0,
            pumpRoom?.Door is not null,
            plan?.Inlets.Count ?? 0,
            plan?.BuildingInlets.Count ?? 0,
            slabBottom,
            ductBottom,
            suggestions,
            importIssues);
    }

    // Оценки исходных данных по геометрии: только то, что однозначно выводится из плана и данных проекта.
    private static List<DrawingSuggestion> Suggestions(ProjectFile project, FloorPlan? plan, double? slabBottom, PumpRoom? pumpRoom, string? normsDir)
    {
        var suggestions = new List<DrawingSuggestion>();
        var design = project.Design;
        if (plan is null || slabBottom is not { } slab)
        {
            return suggestions;
        }

        if (design.FloorElevation is { } floor)
        {
            var roomHeight = slab - floor;
            if (roomHeight > 0.5)
            {
                suggestions.Add(new DrawingSuggestion(
                    "roomHeight",
                    "Высота помещения",
                    Math.Round(roomHeight, 2),
                    "м",
                    $"низ перекрытия {Fmt(slab)} м (слой ПТ_БАЛКИ) − отметка пола {Fmt(floor)} м",
                    "чертёж"));
            }
        }

        var pipeElevation = slab - (design.HangerRodLength / 1000.0);
        suggestions.Add(new DrawingSuggestion(
            "pipeElevation",
            "Отметка трубопроводов",
            Math.Round(pipeElevation, 2),
            "м",
            $"низ перекрытия {Fmt(slab)} м − шпилька подвеса {Fmt(design.HangerRodLength)} мм",
            "чертёж"));

        if (pumpRoom is not null)
        {
            var pumpHeight = slab - design.PumpRoom.FloorElevation;
            if (pumpHeight > 1.0)
            {
                suggestions.Add(new DrawingSuggestion(
                    "pumpRoom.height",
                    "Высота помещения насосной",
                    Math.Round(pumpHeight, 2),
                    "м",
                    $"низ перекрытия {Fmt(slab)} м − отметка пола насосной {Fmt(design.PumpRoom.FloorElevation)} м",
                    "чертёж"));
            }
        }

        if (pumpRoom is not null && normsDir is not null && CollectorAxisMm(normsDir) is { } axisMm)
        {
            var axis = design.PumpRoom.FloorElevation + (axisMm / 1000.0);
            var h = pipeElevation - axis;
            if (h > 0.5)
            {
                suggestions.Add(new DrawingSuggestion(
                    "dictatingAboveAxis",
                    "Высота диктующего оросителя над осью насоса H",
                    Math.Round(h, 2),
                    "м",
                    $"отметка труб {Fmt(pipeElevation)} м − (пол насосной {Fmt(design.PumpRoom.FloorElevation)} м + ось коллектора {Fmt(axisMm)} мм по каталогу); спуск к оросителю не вычтен",
                    "чертёж + каталог"));
            }

            if (plan.Inlets.Count > 0)
            {
                var nearest = plan.Inlets.Min(i => pumpRoom.Outline.DistanceTo(i.Location)) / 1000.0;
                if (nearest >= 0.5)
                {
                    suggestions.Add(new DrawingSuggestion(
                        "supplyLength",
                        "Длина подводящего трубопровода",
                        Math.Round(nearest, 1),
                        "м",
                        "по прямой от контура насосной до ближайшей точки ПТ_ВВОД; трасса с поворотами длиннее — проверьте",
                        "чертёж"));
                }
            }
        }

        return suggestions;
    }

    // Ось коллекторов установки — из каталога (чаще всего встречающееся значение, мм); null — каталога нет.
    private static double? CollectorAxisMm(string normsDir)
    {
        try
        {
            var sets = PumpCatalog.Sets(normsDir);
            return sets.Count == 0 ? null : sets.GroupBy(s => s.CollectorAxisMm).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static (double? Length, double? Width) BoundingBox(Polygon outline)
    {
        var xs = outline.Vertices.Select(v => v.XMm).ToList();
        var ys = outline.Vertices.Select(v => v.YMm).ToList();
        return (Math.Round((xs.Max() - xs.Min()) / 1000.0, 2), Math.Round((ys.Max() - ys.Min()) / 1000.0, 2));
    }

    private static string Fmt(double value) => value.ToString("0.##", Ru);
}
