using System.Text.Json;
using WaterAuto.Export;

namespace WaterAuto.Application;

/// <summary>Том (основной комплект или документ) выпуска: обозначение, наименование и документы по порядку (имена исходных файлов).</summary>
public sealed record TomeEntry(string Designation, string Name, IReadOnlyList<string> Documents);

/// <summary>Опись томов папки выпуска (tome.json): пишется при расчёте, по ней команда pdf собирает том и ИУЛ.</summary>
public sealed record TomeManifest(IReadOnlyList<TomeEntry> Tomes, IReadOnlyList<IulSignature> Signatures, string Font);

/// <summary>
/// Сборка томов в один PDF и ИУЛ по форме 15 ГОСТ Р 21.101-2020 (прил. Х, ADR 0015): PDF документов — рядом с исходными
/// (DrawingPdf, WordPdf), том — «обозначение.pdf», ИУЛ — «обозначение-УЛ.pdf».
/// </summary>
public static class Tome
{
    public const string ManifestFile = "tome.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) };

    /// <summary>Порядок основного комплекта АУПТ: общие данные, листы (как в ведомости), спецификации.</summary>
    public static readonly IReadOnlyList<string> AuptOrder =
    [
        ProjectPipeline.GeneralDataFile, ProjectPipeline.PumpSchemeFile, ProjectPipeline.PumpRoomPlanFile, ProjectPipeline.PumpRoomPipingFile,
        ProjectPipeline.PumpRoomSectionFile, ProjectPipeline.PumpRoomAxonometryFile, ProjectPipeline.InletRouteFile, ProjectPipeline.PlanFile,
        ProjectPipeline.AxonometryFile, "plan_level*", "axonometry_level*", ProjectPipeline.SpecificationDocxFile, ProjectPipeline.PumpStationSpecFile,
    ];

    /// <summary>Порядок основного комплекта АПС: общие данные, схема, планы, кабельный журнал.</summary>
    public static readonly IReadOnlyList<string> AlarmOrder =
    [
        ProjectPipeline.AlarmGeneralDataFile, ProjectPipeline.AlarmSchemeFile, "aps_plan*", ProjectPipeline.CableJournalDocxFile,
    ];

    /// <summary>Документы тома из выпущенных файлов папки — по шаблону порядка («*» — все подходящие по имени, по алфавиту).</summary>
    public static IReadOnlyList<string> Ordered(IEnumerable<string> written, IReadOnlyList<string> order)
    {
        ArgumentNullException.ThrowIfNull(order);
        var names = written.Select(Path.GetFileName).OfType<string>().Distinct().ToList();
        var result = new List<string>();
        foreach (var pattern in order)
        {
            var matches = pattern.EndsWith('*')
                ? names.Where(n => n.StartsWith(pattern[..^1], StringComparison.Ordinal) && Path.GetExtension(n) is ".dxf" or ".dwg").Order(StringComparer.Ordinal)
                : names.Where(n => n == pattern);
            result.AddRange(matches.Where(m => !result.Contains(m)));
        }

        return result;
    }

    public static void WriteManifest(string folder, TomeManifest manifest) =>
        File.WriteAllText(Path.Combine(folder, ManifestFile), JsonSerializer.Serialize(manifest, Json));

    /// <summary>Собирает тома по всем описям папки (с подпапками); возвращает выпущенные файлы (тома и ИУЛ).</summary>
    public static IReadOnlyList<string> Assemble(string folder, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var done = new List<string>();
        foreach (var path in Directory.GetFiles(folder, ManifestFile, SearchOption.AllDirectories))
        {
            var dir = Path.GetDirectoryName(path)!;
            var manifest = JsonSerializer.Deserialize<TomeManifest>(File.ReadAllText(path), Json)!;
            foreach (var tome in manifest.Tomes)
            {
                var pdfs = tome.Documents.Select(d => Path.Combine(dir, Path.ChangeExtension(d, ".pdf"))).ToList();
                var missing = pdfs.Where(p => !File.Exists(p)).Select(Path.GetFileName).ToList();
                if (missing.Count > 0)
                {
                    log($"{tome.Designation}: нет PDF ({string.Join(", ", missing)}) — том не собран.");
                    continue;
                }

                var target = Path.Combine(dir, tome.Designation + ".pdf");
                var pages = IulPdf.Merge(pdfs, target, $"{tome.Designation} {tome.Name}");
                var info = new FileInfo(target);
                var iul = Path.Combine(dir, tome.Designation + "-УЛ.pdf");
                IulPdf.Write(
                    iul,
                    tome.Designation + "-УЛ",
                    [new IulDocument(tome.Designation, tome.Name, "0", "MD5", IulPdf.Md5(target), info.Name, info.LastWriteTime, info.Length)],
                    manifest.Signatures,
                    manifest.Font);
                log($"{target} ({pages} стр.), {Path.GetFileName(iul)}");
                done.Add(target);
                done.Add(iul);
            }
        }

        return done;
    }
}
