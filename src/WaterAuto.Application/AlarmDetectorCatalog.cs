using System.Text.Json;
using WaterAuto.Core;

namespace WaterAuto.Application;

/// <summary>Модель извещателя из каталога (data/catalog/alarm_detectors.json): паспортные данные для выбора в проекте и расчёта.</summary>
public sealed record DetectorModel(string Id, DetectorKind? Kind, string Name, string Designation, string Manufacturer, Current LoopCurrent, int Addresses, bool Isolator, string Source, string? Certificate, string? HeatClass, double? ResponseTemperature)
{
    /// <summary>Ручной извещатель: вид не из <see cref="DetectorKind"/> (ИПР раскладкой пока не размещаются).</summary>
    public bool IsManual => Kind is null;

    public LoopDevice AsLoopDevice() => new($"{Name} ({Designation})", LoopCurrent, Source) { Certificate = Certificate, Addresses = Addresses };
}

/// <summary>Чтение каталога извещателей; схему проверяет tools/ValidateData.</summary>
public static class AlarmDetectorCatalog
{
    public const string File = "alarm_detectors.json";

    public static IReadOnlyList<DetectorModel> Load(string path)
    {
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        var list = new List<DetectorModel>();
        foreach (var d in doc.RootElement.GetProperty("detectors").EnumerateArray())
        {
            DetectorKind? kind = d.GetProperty("kind").GetString() switch
            {
                "smoke" => DetectorKind.Smoke,
                "heat" => DetectorKind.Heat,
                "manual" => null,
                var k => throw new InvalidDataException($"{path}: неизвестный вид извещателя «{k}»."),
            };
            list.Add(new DetectorModel(
                d.GetProperty("id").GetString()!,
                kind,
                d.GetProperty("name").GetString()!,
                d.GetProperty("designation").GetString()!,
                d.GetProperty("manufacturer").GetString()!,
                new Current(d.GetProperty("loopCurrent").GetDouble()),
                d.GetProperty("addresses").GetInt32(),
                d.GetProperty("isolator").GetBoolean(),
                d.GetProperty("source").GetString()!,
                d.TryGetProperty("certificate", out var c) ? c.GetString() : null,
                d.TryGetProperty("heatClass", out var hc) ? hc.GetString() : null,
                d.TryGetProperty("responseTemperature", out var t) ? t.GetDouble() : null));
        }

        return list;
    }

    /// <summary>
    /// Модель для зоны: заданная в проекте (ошибка, если нет в каталоге или другого вида) или первая каталожная нужного вида
    /// (с замечанием — выбор по умолчанию должен быть виден проектировщику).
    /// </summary>
    public static DetectorModel? Choose(IReadOnlyList<DetectorModel> catalog, DetectorKind kind, string? requestedId, string roomName, List<Issue> issues)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(issues);
        if (!string.IsNullOrWhiteSpace(requestedId))
        {
            var model = catalog.FirstOrDefault(m => string.Equals(m.Id, requestedId, StringComparison.Ordinal));
            if (model is null)
            {
                issues.Add(new Issue("aps.detector.unknown", IssueLevel.Error, $"АПС, помещение «{roomName}»: извещателя «{requestedId}» нет в каталоге {File}."));
                return null;
            }

            if (model.Kind != kind)
            {
                issues.Add(new Issue("aps.detector.kindMismatch", IssueLevel.Error, $"АПС, помещение «{roomName}»: извещатель {model.Name} — {KindText(model.Kind)}, а для помещения задан {KindText(kind)} ИП."));
                return null;
            }

            return model;
        }

        var fallback = catalog.FirstOrDefault(m => m.Kind == kind);
        if (fallback is null)
        {
            issues.Add(new Issue("aps.detector.none", IssueLevel.Error, $"АПС, помещение «{roomName}»: в каталоге {File} нет {KindText(kind)} извещателей."));
            return null;
        }

        issues.Add(new Issue("aps.detector.default", IssueLevel.Info, $"АПС, помещение «{roomName}»: модель ИП не выбрана — принят {fallback.Name} ({fallback.Designation}), первый в каталоге; задайте alarm.zones[].model, если нужен другой."));
        return fallback;
    }

    private static string KindText(DetectorKind? kind) => kind switch
    {
        DetectorKind.Smoke => "дымовой",
        DetectorKind.Heat => "тепловой",
        _ => "ручной",
    };
}
