using System.Globalization;
using System.IO;
using System.Text;
using WaterAuto.Application;
using WaterAuto.Core;

namespace WaterAuto.App;

/// <summary>
/// Запрос «быстрого проекта»: чертёж плана и минимум стандартных данных, которые пользователь выбирает в
/// <see cref="QuickProjectWindow"/>. Остальные исходные данные программа берёт из умолчаний модели проекта,
/// каталогов data/ и — по желанию — оценок по геометрии чертежа.
/// </summary>
/// <param name="DrawingPath">Путь к чертежу DWG/DXF (полный или относительный).</param>
/// <param name="RoomGroup">ID строки табл. 6.1 — группа помещений, например «2».</param>
/// <param name="SprinklerId">ID оросителя из data/catalog/sprinklers.json; null — автоподбор при расчёте (K = 0).</param>
/// <param name="PumpSetDesignation">Обозначение комплектной установки из каталога; null — автоподбор по Q и H.</param>
/// <param name="ApplyDrawingSuggestions">Применить оценки исходных данных по геометрии чертежа (высота, отметки, H, подводящий).</param>
public sealed record QuickProjectRequest(
    string DrawingPath,
    string RoomGroup,
    string? SprinklerId,
    string? PumpSetDesignation,
    bool ApplyDrawingSuggestions);

/// <summary>
/// Результат создания файла проекта: путь к <c>*.waproj</c>, был ли проект уже на диске (тогда он не
/// перезаписывается и открывается существующим), какие оценки по чертежу применены и замечания предпроверки.
/// </summary>
public sealed record QuickProjectResult(
    string ProjectPath,
    bool AlreadyExisted,
    IReadOnlyList<string> AppliedSuggestions,
    IReadOnlyList<Issue> Issues);

/// <summary>
/// Сценарий «Быстрый проект»: по чертежу и минимуму стандартных данных создаёт файл <c>*.waproj</c> рядом с
/// чертежом, подставляет оценки по геометрии плана (<see cref="DrawingPreview"/>) и возвращает проект главному
/// окну. Расчёт здесь не запускается — его выполняет главное окно после открытия проекта.
/// <para>
/// Нормативных чисел в коде нет: группа помещений, ороситель и установка приходят из запроса/каталогов data/,
/// а оценки по чертежу — из <see cref="DrawingPreview"/> с основанием расчёта. Существующий файл проекта
/// не перезаписывается: вызывающий получает <see cref="QuickProjectResult.AlreadyExisted"/> и открывает его.
/// </para>
/// </summary>
public static class QuickProject
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>
    /// Создать файл проекта. <paramref name="root"/> — корень установки/репозитория, в котором есть <c>data/norms</c>
    /// (обычно <see cref="DataLocator.FindRoot"/>); из него читаются каталоги для выбранного оросителя и для
    /// оценок по чертежу.
    /// </summary>
    /// <exception cref="ArgumentException">Пустой чертёж, группа помещений или корень данных.</exception>
    /// <exception cref="InvalidDataException">Чертёж не найден, не DWG/DXF или файл проекта записать не удалось.</exception>
    public static QuickProjectResult Create(QuickProjectRequest request, string root)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(root, "Не задан корень данных установки (папка с data/norms).");

        var drawing = FullDrawingPath(request.DrawingPath);
        if (string.IsNullOrEmpty(request.RoomGroup) || request.RoomGroup.Trim().Length == 0)
        {
            throw new ArgumentException("Не задана группа помещений: без неё расчёт не начнётся.", nameof(request));
        }

        if (!File.Exists(drawing))
        {
            throw new InvalidDataException($"Файл чертежа не найден: {drawing}");
        }

        var extension = Path.GetExtension(drawing);
        if (!string.Equals(extension, ".dwg", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".dxf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Чертёж должен быть в формате DWG или DXF, а не «{extension}»: {drawing}");
        }

        var projectPath = Path.ChangeExtension(drawing, ".waproj");
        if (File.Exists(projectPath))
        {
            return new QuickProjectResult(projectPath, AlreadyExisted: true, AppliedSuggestions: [], Issues: []);
        }

        var normsDir = DataLocator.Norms(root);
        var issues = new List<Issue>();
        var design = new ProjectDesign
        {
            RoomGroup = request.RoomGroup.Trim(),
            PumpStation = new ProjectPumpStation { SetDesignation = request.PumpSetDesignation?.Trim() ?? "" },
        };

        if (!string.IsNullOrWhiteSpace(request.SprinklerId))
        {
            design = ApplySprinkler(design, request.SprinklerId.Trim(), normsDir, issues);
        }

        var project = new ProjectFile
        {
            Name = Path.GetFileNameWithoutExtension(drawing),
            Drawing = Path.GetFileName(drawing),
            Design = design,
            Title = TitleFromProfile(Path.GetFileNameWithoutExtension(drawing)),
        };

        var applied = new List<string>();
        if (request.ApplyDrawingSuggestions)
        {
            (project, applied) = ApplyDrawingSuggestions(project, drawing, normsDir, issues);
        }

        try
        {
            WriteProject(projectPath, project);
        }
        catch (IOException) when (File.Exists(projectPath))
        {
            // Файл проекта успели создать между проверкой и записью: он не перезаписывается.
            return new QuickProjectResult(projectPath, AlreadyExisted: true, AppliedSuggestions: [], Issues: []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Не удалось записать файл проекта {projectPath}: {ex.Message}", ex);
        }

        return new QuickProjectResult(projectPath, AlreadyExisted: false, applied, issues);
    }

    /// <summary>Полный путь к чертежу; битый путь — понятная ошибка вместо системной.</summary>
    private static string FullDrawingPath(string? path)
    {
        var value = path?.Trim() ?? "";
        if (value.Length == 0)
        {
            throw new ArgumentException("Не задан чертёж плана: укажите файл DWG или DXF.", nameof(path));
        }

        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"Путь к чертежу некорректен: {value}", ex);
        }
    }

    /// <summary>
    /// Ороситель из каталога data/catalog: K, модель и положение розетки переносятся в проект. Если каталог
    /// не прочитан или ID не найден — проект создаётся с автоподбором (K = 0), причина попадает в замечания.
    /// </summary>
    private static ProjectDesign ApplySprinkler(ProjectDesign design, string sprinklerId, string normsDir, List<Issue> issues)
    {
        try
        {
            var sprinkler = InputCatalog.Sprinklers(normsDir)
                .FirstOrDefault(s => string.Equals(s.Id, sprinklerId, StringComparison.Ordinal));
            if (sprinkler is null)
            {
                issues.Add(new Issue(
                    "quick.sprinklerNotFound",
                    IssueLevel.Warning,
                    $"Ороситель «{sprinklerId}» не найден в каталоге data/catalog: K = 0, модель и K подберёт расчёт по каталогу."));
                return design;
            }

            return design with
            {
                SprinklerK = sprinkler.K,
                SprinklerModel = sprinkler.Model,
                SprinklerDeflector = KnownDeflector(sprinkler.Deflector) ?? design.SprinklerDeflector,
            };
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            issues.Add(new Issue(
                "quick.catalogUnreadable",
                IssueLevel.Warning,
                $"Каталог оросителей прочитать не удалось ({ex.Message}): K = 0, модель и K подберёт расчёт."));
            return design;
        }
    }

    /// <summary>
    /// Оценки по геометрии чертежа: <see cref="DrawingPreview.Analyze"/> предлагает значения, а проект получает те,
    /// для которых известно поле модели. Сбой разбора не отменяет создание проекта — замечание возвращается вызывающему.
    /// </summary>
    private static (ProjectFile Project, List<string> Applied) ApplyDrawingSuggestions(
        ProjectFile project,
        string drawing,
        string normsDir,
        List<Issue> issues)
    {
        var applied = new List<string>();
        DrawingPreviewResult preview;
        try
        {
            preview = DrawingPreview.Analyze(project, drawing, normsDir);
        }
#pragma warning disable CA1031 // создание проекта не должно падать из-за разбора чертежа: чертёж уже проверен, оценки — удобство
        catch (Exception ex)
#pragma warning restore CA1031
        {
            issues.Add(new Issue(
                "quick.previewFailed",
                IssueLevel.Warning,
                $"Оценки по чертежу не применены: предпроверка не выполнена ({ex.Message}). Проект создан с умолчаниями."));
            return (project, applied);
        }

        issues.AddRange(preview.Issues);
        var design = project.Design;
        foreach (var suggestion in preview.Suggestions)
        {
            if (ApplySuggestion(design, suggestion) is not { } updated)
            {
                continue;
            }

            design = updated;
            applied.Add(string.Create(Ru, $"{suggestion.Label} = {suggestion.Value:0.##} {suggestion.Unit}"));
        }

        return (project with { Design = design }, applied);
    }

    /// <summary>Перенос оценки в поле проекта; <c>null</c> — для этого ключа в модели проекта нет поля.</summary>
    private static ProjectDesign? ApplySuggestion(ProjectDesign design, DrawingSuggestion suggestion) => suggestion.Key switch
    {
        "floorElevation" => design with { FloorElevation = suggestion.Value },
        "roomHeight" => design with { RoomHeight = suggestion.Value },
        "pipeElevation" => design with { PipeElevation = suggestion.Value },
        "dictatingAboveAxis" => design with { DictatingAboveAxis = suggestion.Value },
        "supplyLength" => design with { SupplyLength = suggestion.Value },
        "pumpRoom.floorElevation" => design with { PumpRoom = WithFloor(design.PumpRoom, suggestion.Value) },
        "pumpRoom.height" => design with { PumpRoom = WithHeight(design.PumpRoom, suggestion.Value) },
        _ => null,
    };

    /// <summary>Копия помещения насосной с новой отметкой пола: <see cref="ProjectPumpRoom"/> — класс, не запись.</summary>
    private static ProjectPumpRoom WithFloor(ProjectPumpRoom room, double floorElevation) => new()
    {
        FloorElevation = floorElevation,
        GroundElevation = room.GroundElevation,
        InletDepth = room.InletDepth,
        Height = room.Height,
        WallThickness = room.WallThickness,
        Underground = room.Underground,
        PitSize = room.PitSize,
        PitDepth = room.PitDepth,
        InletRouteElevation = room.InletRouteElevation,
        InletRouteClearance = room.InletRouteClearance,
    };

    /// <summary>Копия помещения насосной с новой высотой: <see cref="ProjectPumpRoom"/> — класс, не запись.</summary>
    private static ProjectPumpRoom WithHeight(ProjectPumpRoom room, double height) => new()
    {
        FloorElevation = room.FloorElevation,
        GroundElevation = room.GroundElevation,
        InletDepth = room.InletDepth,
        Height = height,
        WallThickness = room.WallThickness,
        Underground = room.Underground,
        PitSize = room.PitSize,
        PitDepth = room.PitDepth,
        InletRouteElevation = room.InletRouteElevation,
        InletRouteClearance = room.InletRouteClearance,
    };

    /// <summary>
    /// Титульный лист быстрого проекта: профиль последнего проекта (организация, автор, шифр и т.д.) —
    /// чтобы не вводить их заново; пустые поля профиля остаются умолчаниями модели.
    /// </summary>
    private static ProjectTitle TitleFromProfile(string objectName)
    {
        var settings = ThemeManager.Settings;
        var title = new ProjectTitle();
        if (settings.TitleOrganization.Length > 0)
        {
            title = title with { Organization = settings.TitleOrganization };
        }

        if (settings.TitleAuthor.Length > 0)
        {
            title = title with { Author = settings.TitleAuthor };
        }

        if (settings.TitleSupervisor.Length > 0)
        {
            title = title with { Supervisor = settings.TitleSupervisor };
        }

        if (settings.TitleCityYear.Length > 0)
        {
            title = title with { CityYear = settings.TitleCityYear };
        }

        if (settings.TitleCode.Length > 0)
        {
            title = title with { Code = settings.TitleCode };
        }

        if (settings.TitleStage.Length > 0)
        {
            title = title with { Stage = settings.TitleStage };
        }

        // Наименование здания: профиль, а в быстром проекте по умолчанию — имя чертежа (обычно это и есть объект).
        title = settings.TitleBuilding.Length > 0
            ? title with { Building = settings.TitleBuilding }
            : title with { Building = objectName };
        return title;
    }

    /// <summary>
    /// Запись проекта без риска оставить битый файл: сначала временный файл с проверкой чтением модели
    /// (как <see cref="ProjectJson.Save"/>), затем атомарное перемещение на место. Существующий проект не перезаписывается.
    /// </summary>
    private static void WriteProject(string projectPath, ProjectFile project)
    {
        var temp = projectPath + ".tmp";
        try
        {
            File.WriteAllText(temp, ProjectFile.ToJson(project), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            ProjectFile.Load(temp);
            File.Move(temp, projectPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>Положение розетки, которое знает модель проекта; иное значение оставляет умолчание.</summary>
    internal static string? KnownDeflector(string deflector) => deflector switch
    {
        "up" => "up",
        "down" => "down",
        "horizontal" => "horizontal",
        "concealed" => "concealed",
        _ => null,
    };

    /// <summary>Сбой чтения данных data/ (нормы, каталоги) — не ошибка программы: проект создаётся с замечанием.</summary>
    internal static bool IsDataError(Exception ex) =>
        ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or InvalidOperationException or FormatException or KeyNotFoundException;
}
