using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WaterAuto.Application;

/// <summary>
/// Правка файла проекта без ручного JSON: чтение со слиянием с умолчаниями модели, запись с проверкой
/// (сохранённый файл должен читаться <see cref="ProjectFile.Load"/>) и атомарной заменой.
/// Нормы здесь не задаются — только данные объекта и решения проектировщика.
/// </summary>
public static class ProjectJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Проект как JSON со значениями по умолчанию: отсутствующие в файле поля видны и сохраняются явно.</summary>
    public static JsonObject LoadMerged(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var actual = JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) as JsonObject
            ?? throw new InvalidDataException($"Файл проекта не является JSON-объектом: {path}");
        var defaults = JsonNode.Parse(ProjectFile.ToJson(new ProjectFile()), documentOptions: ReadOptions) as JsonObject ?? new JsonObject();
        return Merge(defaults, actual);
    }

    /// <summary>Слияние фактических значений поверх умолчаний; вложенные объекты сливаются, листья заменяются.</summary>
    public static JsonObject Merge(JsonObject target, JsonObject actual)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(actual);
        foreach (var (key, value) in actual)
        {
            if (value is JsonObject actualObject && target[key] is JsonObject targetObject)
            {
                Merge(targetObject, actualObject);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }

        return target;
    }

    /// <summary>Запись проекта: сохраняется только после проверки, что файл читается моделью; при ошибке файл не меняется.</summary>
    public static void Save(string path, JsonObject root)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(root);
        var json = root.ToJsonString(Options);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            ProjectFile.Load(temp);
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
