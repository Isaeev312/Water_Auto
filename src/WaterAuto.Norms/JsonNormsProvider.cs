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

    public static JsonNormsProvider Load(string normsDirectory)
    {
        var records = new Dictionary<string, NormRecord>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(normsDirectory, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var docName = root.GetProperty("doc").GetString() ?? "";
            var edition = root.GetProperty("edition").GetString() ?? "";
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
                if (!records.TryAdd(id, new NormRecord(id, docName, edition, clause, values, units, attrs)))
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
}
