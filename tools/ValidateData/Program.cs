// Проверка всех JSON в data/: каждый файл объявляет свою схему в поле "$schema"
// (путь относительно файла), файл проверяется по ней. Для норм — дополнительные
// проверки, которые схема выразить не может.
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Использование: ValidateData <папка data>");
    return 2;
}

var dataDir = Path.GetFullPath(args[0]);
var schemasDir = Path.Combine(dataDir, "schemas");
var schemas = new Dictionary<string, JsonSchema>(StringComparer.OrdinalIgnoreCase);
var normIds = new Dictionary<string, string>(StringComparer.Ordinal);
var errors = new List<string>();
var checkedFiles = 0;

foreach (var file in Directory.EnumerateFiles(dataDir, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
{
    var rel = Path.GetRelativePath(dataDir, file).Replace('\\', '/');
    var text = File.ReadAllText(file);
    JsonNode? doc;
    try
    {
        doc = JsonNode.Parse(text);
    }
    catch (JsonException ex)
    {
        errors.Add($"{rel}: не JSON — {ex.Message}");
        continue;
    }

    if (file.StartsWith(schemasDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    {
        continue;
    }

    checkedFiles++;
    var schemaRef = doc?["$schema"]?.GetValue<string>();
    if (string.IsNullOrEmpty(schemaRef))
    {
        errors.Add($"{rel}: нет поля \"$schema\"");
        continue;
    }

    var schemaPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, schemaRef));
    if (!File.Exists(schemaPath))
    {
        errors.Add($"{rel}: схема не найдена: {schemaRef}");
        continue;
    }

    if (!schemas.TryGetValue(schemaPath, out var schema))
    {
        schema = JsonSchema.FromFile(schemaPath);
        schemas[schemaPath] = schema;
    }

    using var element = JsonDocument.Parse(text);
    var result = schema.Evaluate(element.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
    if (!result.IsValid)
    {
        foreach (var detail in (result.Details ?? []).Prepend(result))
        {
            foreach (var error in detail.Errors ?? new Dictionary<string, string>())
            {
                errors.Add($"{rel}: {detail.InstanceLocation} — {error.Value}");
            }
        }

        continue;
    }

    if (Path.GetFileName(schemaPath).Equals("norms.schema.json", StringComparison.OrdinalIgnoreCase))
    {
        CheckNorms(rel, doc!, normIds, errors);
    }
}

foreach (var error in errors)
{
    Console.Error.WriteLine(error);
}

Console.WriteLine($"Проверено файлов: {checkedFiles}, записей норм: {normIds.Count}, ошибок: {errors.Count}.");
return errors.Count == 0 ? 0 : 1;

static void CheckNorms(string rel, JsonNode doc, Dictionary<string, string> ids, List<string> errors)
{
    foreach (var record in doc["records"]!.AsArray())
    {
        var id = record!["id"]!.GetValue<string>();
        if (!ids.TryAdd(id, rel))
        {
            errors.Add($"{rel}: id {id} уже есть в {ids[id]}");
        }

        var values = record["values"]!.AsObject();
        var units = record["units"]!.AsObject();
        var refs = record["refs"]?.AsObject();
        var hasNote = !string.IsNullOrWhiteSpace(record["note"]?.GetValue<string>());

        foreach (var (key, value) in values)
        {
            if (!units.ContainsKey(key))
            {
                errors.Add($"{rel}: {id}: нет единицы для {key}");
            }

            if (value is null && refs?.ContainsKey(key) != true && !hasNote)
            {
                errors.Add($"{rel}: {id}: {key} = null без refs и note");
            }
        }

        foreach (var (key, _) in units)
        {
            if (!values.ContainsKey(key))
            {
                errors.Add($"{rel}: {id}: единица для несуществующего ключа {key}");
            }
        }

        if (refs is not null)
        {
            foreach (var (key, _) in refs)
            {
                if (!values.TryGetPropertyValue(key, out var v) || v is not null)
                {
                    errors.Add($"{rel}: {id}: refs.{key} допустим только для значения null");
                }
            }
        }
    }
}
