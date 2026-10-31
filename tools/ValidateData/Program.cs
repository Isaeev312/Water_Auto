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
var normEditions = new Dictionary<(string Doc, string Clause), (string Edition, string Rel)>();
var normValues = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
var profiles = new List<(string Rel, JsonNode Doc)>();
var errors = new List<string>();
var checkedFiles = 0;
var unverified = new List<string>();

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
        CheckSingleEdition(rel, doc!, normEditions, errors);
        CheckRequiredKeys(rel, doc!, errors);
        foreach (var record in doc!["records"]!.AsArray())
        {
            normValues[record!["id"]!.GetValue<string>()] = record["values"]!.AsObject();
        }
    }
    else if (Path.GetFileName(schemaPath).Equals("profile.schema.json", StringComparison.OrdinalIgnoreCase))
    {
        profiles.Add((rel, doc!));
    }
    else if (Path.GetFileName(schemaPath).Equals("sprinklers.schema.json", StringComparison.OrdinalIgnoreCase))
    {
        CheckSprinklers(rel, doc!, errors);
    }

    if (doc!["verified"]?.GetValue<bool>() == false)
    {
        unverified.Add(rel);
    }
}

// Профили проверяются после всех норм: профиль может только ужесточать норму (ARCHITECTURE §9.1).
foreach (var (rel, doc) in profiles)
{
    CheckProfile(rel, doc, normValues, errors);
}

foreach (var error in errors)
{
    Console.Error.WriteLine(error);
}

Console.WriteLine($"Проверено файлов: {checkedFiles}, записей норм: {normIds.Count}, ошибок: {errors.Count}.");
if (unverified.Count > 0)
{
    // Не ошибка: данные перенесены, но специалист их не сверял. Перед выпуском список должен быть пустым.
    Console.WriteLine($"Не сверено специалистом (verified: false): {unverified.Count} — {string.Join(", ", unverified)}.");
}

return errors.Count == 0 ? 0 : 1;

static void CheckSprinklers(string rel, JsonNode doc, List<string> errors)
{
    // K в q = 10·K·√P (л/с, МПа) и K-фактор в л/(мин·бар^0,5) связаны только единицами: K = K_lpm / (60·√10).
    // Расхождение больше допуска округления каталога — ошибка переноса.
    var lpmToCore = 60 * Math.Sqrt(10);
    const double tolerance = 0.02;
    var ids = new HashSet<string>(StringComparer.Ordinal);
    foreach (var sprinkler in doc["sprinklers"]!.AsArray())
    {
        var id = sprinkler!["id"]!.GetValue<string>();
        if (!ids.Add(id))
        {
            errors.Add($"{rel}: id {id} повторяется");
        }

        var k = sprinkler["K"]!.GetValue<double>();
        if (sprinkler["K_lpm"] is { } kLpm)
        {
            var expected = kLpm.GetValue<double>() / lpmToCore;
            if (Math.Abs(k - expected) / expected > tolerance)
            {
                errors.Add($"{rel}: {id}: K = {k} не согласуется с K_lpm = {kLpm} (ожидается ≈ {expected:0.000})");
            }
        }

        if (sprinkler["pMin"]!.GetValue<double>() >= sprinkler["pMax"]!.GetValue<double>())
        {
            errors.Add($"{rel}: {id}: pMin должен быть меньше pMax");
        }

        foreach (var point in sprinkler["intensity"]?.AsArray() ?? [])
        {
            var p = point!["P"]!.GetValue<double>();
            var i = point["i"]!.GetValue<double>();
            if (p <= 0 || i <= 0)
            {
                errors.Add($"{rel}: {id}: точка интенсивности P = {p}, i = {i} должна быть положительной");
            }
        }
    }
}

static void CheckSingleEdition(string rel, JsonNode doc, Dictionary<(string Doc, string Clause), (string Edition, string Rel)> files, List<string> errors)
{
    // В одной папке норм не может быть двух файлов одного документа и пункта в разных редакциях:
    // JsonNormsProvider.Load без явной редакции не может выбрать, какую брать. Проверка — по doc и
    // clause уровня файла (уровень записи здесь не рассматривается).
    var docName = doc["doc"]!.GetValue<string>();
    var edition = doc["edition"]!.GetValue<string>();
    if (doc["clause"]?.GetValue<string>() is not { } clause)
    {
        return;
    }

    var key = (docName, clause);
    if (!files.TryGetValue(key, out var existing))
    {
        files[key] = (edition, rel);
        return;
    }

    if (existing.Edition != edition)
    {
        errors.Add($"{rel}: документ «{docName}», {clause} в редакции «{edition}», а {existing.Rel} — в редакции «{existing.Edition}»; в одной папке норм не может быть двух редакций");
    }
}

static void CheckRequiredKeys(string rel, JsonNode doc, List<string> errors)
{
    // Обязательные ключи записей норм: clause → ключи values → ключи attrs. Это единственное место,
    // где перечислены списки: при переименовании ключа в data/norms ошибку должен дать ValidateData,
    // а не расчёт с KeyNotFoundException/NullReferenceException.
    // Источник списков:
    //   «табл. Б.2» и «табл. Б.3» (строка quantity == "Kt") — WaterAuto.Calc/Hydraulics/PipeCharacteristics.cs,
    //     Resolve: standard, D_out, wall, Kt и standard, quantity, formula, psi, theta, dnMin, dnMax; dn в Б.2 —
    //     признак строки таблицы (сверку dn с D_out делает этап «Граф сети», разбор п. 2.4);
    //   «Б.1.3.13» — localLossFactor, доля местных потерь (appendix_B_clauses.json; README §6.1, ADR 0002);
    //   «табл. 6.1» — параметры АУП по группе помещения (README §6.1): i, Q, S, t, шаг и attrs.group.
    //     В текущем срезе Calc эти две записи ещё не читает: списки закрепляют их формат заранее.
    //   «прил. Б, п. Б.2» — t, время работы от АКБ (sp6/appendix_B.json; WaterAuto.Alarm/BatteryCapacity.cs).
    // Строка «табл. Б.3» с quantity == "A" (формула Б.7) сюда не входит: Calc её не использует.
    (string Clause, string? SelectAttr, string? SelectValue, string[] Values, string[] Attrs)[] requirements =
    [
        ("табл. Б.2", null, null, ["dn", "D_out", "wall", "Kt"], ["standard"]),
        ("табл. Б.3", "quantity", "Kt", ["psi", "theta", "dnMin", "dnMax"], ["standard", "formula", "quantity"]),
        ("Б.1.3.13", null, null, ["localLossFactor"], []),
        ("табл. 6.1", null, null, ["i_water", "i_foam", "Q_water", "Q_foam", "S", "t_min", "maxSpacing"], ["group"]),
        ("прил. Б, п. Б.2", null, null, ["t"], []),
    ];

    foreach (var node in doc["records"]!.AsArray())
    {
        var record = node!;
        var id = record["id"]!.GetValue<string>();
        var clause = record["clause"]!.GetValue<string>();
        var attrs = record["attrs"] as JsonObject;
        foreach (var requirement in requirements)
        {
            if (requirement.Clause != clause)
            {
                continue;
            }

            // Для табл. Б.3 обязательные ключи заданы только для строки Kt; строка формулы Б.7 не проверяется.
            // Если сам ключ-селектор пропал, сказать об этом: иначе переименование quantity молча выключит проверку.
            if (requirement.SelectAttr is { } selectAttr)
            {
                if (attrs is null || !attrs.TryGetPropertyValue(selectAttr, out var selector))
                {
                    errors.Add($"{rel}: {id}: нет ключа attrs.{selectAttr} (по нему выбираются обязательные ключи clause «{clause}»)");
                    continue;
                }

                if (selector is not JsonValue selectorValue
                    || !selectorValue.TryGetValue<string>(out var selectorText)
                    || selectorText != requirement.SelectValue)
                {
                    continue;
                }
            }

            var values = record["values"]!.AsObject();
            foreach (var key in requirement.Values)
            {
                if (!values.ContainsKey(key))
                {
                    errors.Add($"{rel}: {id}: нет обязательного ключа values.{key} (clause «{clause}»)");
                }
            }

            foreach (var key in requirement.Attrs)
            {
                if (attrs?.ContainsKey(key) != true)
                {
                    errors.Add($"{rel}: {id}: нет обязательного ключа attrs.{key} (clause «{clause}»)");
                }
            }
        }
    }
}

static void CheckProfile(string rel, JsonNode doc, Dictionary<string, JsonObject> norms, List<string> errors)
{
    var ids = new HashSet<string>(StringComparer.Ordinal);
    foreach (var record in doc["records"]!.AsArray())
    {
        var id = record!["id"]!.GetValue<string>();
        if (!ids.Add(id))
        {
            errors.Add($"{rel}: id {id} повторяется");
        }

        var values = record["values"]?.AsObject();
        var units = record["units"]?.AsObject();
        foreach (var (key, _) in values ?? [])
        {
            if (units?.ContainsKey(key) != true)
            {
                errors.Add($"{rel}: {id}: нет единицы для {key}");
            }
        }

        var tightens = record["tightens"];
        if (tightens is null)
        {
            continue;
        }

        var normId = tightens["norm"]!.GetValue<string>();
        var key2 = tightens["key"]!.GetValue<string>();
        var bound = tightens["bound"]!.GetValue<string>();
        if (!norms.TryGetValue(normId, out var normRecord))
        {
            errors.Add($"{rel}: {id}: нет записи нормы {normId}");
            continue;
        }

        var normValue = normRecord[key2]?.GetValue<double>();
        var profileValue = values?[key2]?.GetValue<double>();
        if (normValue is null || profileValue is null)
        {
            errors.Add($"{rel}: {id}: нет значения для сравнения с {normId}.{key2}");
        }
        else if (bound == "max" ? profileValue > normValue : profileValue < normValue)
        {
            errors.Add($"{rel}: {id}: профиль ослабляет норму {normId}.{key2} ({profileValue} против {normValue})");
        }
    }
}

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
