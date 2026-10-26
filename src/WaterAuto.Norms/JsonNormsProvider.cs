using System.Text.Json;
using WaterAuto.Core;

namespace WaterAuto.Norms;

/// <summary>
/// Нормы из файлов data/norms/**/*.json (формат README §6.1). Схему файлов проверяет tools/ValidateData;
/// здесь — только чтение. Документ и редакция берутся с уровня файла.
/// </summary>
public sealed class JsonNormsProvider : INormsProvider
{
    private readonly Dictionary<string, NormRecord> _records;

    private JsonNormsProvider(Dictionary<string, NormRecord> records) => _records = records;

    /// <summary>
    /// Загружает нормы из папки. Если редакция задана, читаются только файлы с этой редакцией.
    /// Если не задана — все файлы, но смешение редакций одного документа в папке запрещено:
    /// провайдер не может выбрать редакцию сам, это ошибка конфигурации (как повтор id), а не расчёта.
    /// </summary>
    public static JsonNormsProvider Load(string normsDirectory, string? edition = null)
    {
        var files = Directory.EnumerateFiles(normsDirectory, "*.json", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(file => (Path: file, Text: File.ReadAllText(file)))
            .ToList();

        if (edition is null)
        {
            RejectMixedEditions(normsDirectory, files);
        }

        var records = new Dictionary<string, NormRecord>(StringComparer.Ordinal);
        foreach (var (file, text) in files)
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var docName = root.GetProperty("doc").GetString() ?? "";
            var fileEdition = root.GetProperty("edition").GetString() ?? "";
            if (edition is not null && !string.Equals(fileEdition, edition, StringComparison.Ordinal))
            {
                continue;
            }

            var fileClause = root.TryGetProperty("clause", out var c) ? c.GetString() : null;
            foreach (var r in root.GetProperty("records").EnumerateArray())
            {
                var id = r.GetProperty("id").GetString()!;
                var values = new Dictionary<string, double?>(StringComparer.Ordinal);
                foreach (var v in r.GetProperty("values").EnumerateObject())
                {
                    values[v.Name] = v.Value.ValueKind == JsonValueKind.Null ? null : v.Value.GetDouble();
                }

                var units = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var u in r.GetProperty("units").EnumerateObject())
                {
                    units[u.Name] = u.Value.GetString() ?? "";
                }

                var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
                if (r.TryGetProperty("attrs", out var a))
                {
                    foreach (var at in a.EnumerateObject())
                    {
                        attrs[at.Name] = at.Value.ValueKind == JsonValueKind.String ? at.Value.GetString()! : at.Value.GetRawText();
                    }
                }

                var clause = r.TryGetProperty("clause", out var rc) ? rc.GetString()! : fileClause ?? "";
                if (!records.TryAdd(id, new NormRecord(id, docName, fileEdition, clause, values, units, attrs)))
                {
                    throw new InvalidDataException($"Повтор id нормы {id} в {file}.");
                }
            }
        }

        return new JsonNormsProvider(records);
    }

    public NormRecord? Find(string id) => _records.GetValueOrDefault(id);

    public IEnumerable<NormRecord> ByClause(string clause) =>
        _records.Values.Where(r => r.Clause == clause);

    /// <summary>
    /// Без явной редакции смешение редакций одного документа недопустимо: непонятно, какую брать.
    /// Сообщение перечисляет все редакции (и файлы), чтобы конфигурацию можно было исправить.
    /// </summary>
    private static void RejectMixedEditions(string normsDirectory, IReadOnlyList<(string Path, string Text)> files)
    {
        var editions = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (path, text) in files)
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var docName = root.GetProperty("doc").GetString() ?? "";
            var fileEdition = root.GetProperty("edition").GetString() ?? "";
            if (!editions.TryGetValue(docName, out var byEdition))
            {
                byEdition = new Dictionary<string, string>(StringComparer.Ordinal);
                editions[docName] = byEdition;
            }

            byEdition.TryAdd(fileEdition, path);
        }

        var mixed = editions.Where(pair => pair.Value.Count > 1).ToList();
        if (mixed.Count == 0)
        {
            return;
        }

        var details = string.Join("; ", mixed.Select(pair =>
            $"документ «{pair.Key}» — " + string.Join(", ", pair.Value.Select(e => $"«{e.Key}» ({e.Value})"))));
        throw new InvalidDataException(
            $"В папке норм {normsDirectory} встретились разные редакции одного документа: {details}. " +
            "Редакцию нужно указать явно: JsonNormsProvider.Load(normsDirectory, edition).");
    }
}
