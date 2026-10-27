using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WaterAuto.Application;

namespace WaterAuto.App.InputData;

/// <summary>
/// Списки допустимых значений и умолчания модели проекта для мастера исходных данных. Всё, что можно взять
/// из data/, берётся оттуда: нормы (группы помещений, строки табл. Ж.1) и каталоги (оросители, трубы, насосные
/// установки). Если данных нет — списки пустые, поля работают как текстовые, ошибки не возникает.
/// </summary>
internal sealed class InputSources
{
    /// <summary>DN, которые встречаются в практике АУПТ помимо сортамента data/catalog/pipe_sizes.json.</summary>
    private static readonly int[] StandardDns = [15, 20, 25, 32, 40, 50, 65, 80, 100, 125, 150];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private InputSources()
    {
    }

    /// <summary>Найдена и прочитана папка data/norms.</summary>
    public bool HasNorms { get; private set; }

    /// <summary>Часть списков загрузить не удалось (файл повреждён, нет прав); окно показывает предупреждение.</summary>
    public string? LoadError { get; private set; }

    public InputCatalog.NormsInfo? Norms { get; private set; }

    public IReadOnlyList<InputCatalog.RoomGroupOption> RoomGroups { get; private set; } = [];

    public IReadOnlyList<InputCatalog.SprinklerOption> Sprinklers { get; private set; } = [];

    public IReadOnlyList<InputCatalog.PipeStandardOption> PipeStandards { get; private set; } = [];

    public IReadOnlyList<int> PipeDns { get; private set; } = [];

    public IReadOnlyList<InputCatalog.PumpSetOption> PumpSets { get; private set; } = [];

    public IReadOnlyList<InputCatalog.HydrantRowOption> HydrantRows { get; private set; } = [];

    /// <summary>Скорость подбора диаметров по умолчанию из data/catalog/pipe_sizes.json (defaultDesignVelocity).</summary>
    public double? DefaultDesignVelocity { get; private set; }

    /// <summary>Все DN для выпадающих списков: сортамент из data/ плюс ходовые размеры, по возрастанию.</summary>
    public IReadOnlyList<int> DnOptions { get; private set; } = StandardDns;

    /// <summary>Умолчания модели проекта (camelCase, как в файле) — для подсказок «по умолчанию».</summary>
    public JsonObject ModelDefaults { get; } = LoadModelDefaults();

    /// <summary>Умолчания необязательных объектов, которых нет в файле проекта (ВПВ, резервуар).</summary>
    public static JsonObject? DefaultsFor(string path) => path switch
    {
        "design.hydrants" => Serialize(new ProjectHydrants()),
        "design.tank" => Serialize(new ProjectTank()),
        _ => null,
    };

    public static InputSources Load(string projectPath)
    {
        var sources = new InputSources();
        string? error = null;
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return sources;
        }

        try
        {
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            var root = projectDirectory is null ? null : DataLocator.FindRoot(projectDirectory, AppContext.BaseDirectory);
            if (root is not null)
            {
                var norms = DataLocator.Norms(root);
                if (Directory.Exists(norms))
                {
                    sources.HasNorms = true;
                    sources.RoomGroups = TryList(() => InputCatalog.RoomGroups(norms), ref error);
                    sources.Sprinklers = TryList(() => InputCatalog.Sprinklers(norms), ref error);
                    sources.PipeStandards = TryList(() => InputCatalog.PipeStandards(norms), ref error);
                    sources.PipeDns = TryList(() => InputCatalog.PipeDns(norms), ref error);
                    sources.PumpSets = TryList(() => InputCatalog.PumpSets(norms), ref error);
                    sources.HydrantRows = TryList(() => InputCatalog.HydrantRows(norms), ref error);
                    sources.Norms = TryOne(() => InputCatalog.Norms(norms), ref error);
                    try
                    {
                        sources.DefaultDesignVelocity = ReadDefaultVelocity(norms);
                    }
                    catch (Exception ex) when (IsDataError(ex))
                    {
                        error ??= ex.Message;
                    }

                    sources.DnOptions = [.. sources.PipeDns.Union(StandardDns).Order()];
                }
            }
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            error ??= ex.Message;
        }

        sources.LoadError = error;
        return sources;
    }

    private static IReadOnlyList<T> TryList<T>(Func<IReadOnlyList<T>> load, ref string? error)
    {
        try
        {
            return load();
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            error ??= ex.Message;
            return [];
        }
    }

    private static T? TryOne<T>(Func<T?> load, ref string? error)
        where T : class
    {
        try
        {
            return load();
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            error ??= ex.Message;
            return null;
        }
    }

    private static bool IsDataError(Exception ex) =>
        ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException
            or FormatException or ArgumentException or InvalidOperationException or KeyNotFoundException;

    private static double? ReadDefaultVelocity(string normsDir)
    {
        var path = Path.Combine(InputCatalog.CatalogDir(normsDir), "pipe_sizes.json");
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty("defaultDesignVelocity", out var value) && value.TryGetDouble(out var velocity)
            ? velocity
            : null;
    }

    private static JsonObject LoadModelDefaults()
    {
        try
        {
            return JsonNode.Parse(ProjectFile.ToJson(new ProjectFile())) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static JsonObject? Serialize(object value) =>
        JsonSerializer.SerializeToNode(value, JsonOptions) as JsonObject;
}
