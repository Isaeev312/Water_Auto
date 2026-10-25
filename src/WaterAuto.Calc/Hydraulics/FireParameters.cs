using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

public sealed record FireParametersResult(FireParameters? Parameters, IReadOnlyList<Issue> Issues);

/// <summary>
/// Параметры спринклерной АУП на воде по группе помещений, высоте помещения и высоте складирования (п. 6.1.4):
/// группы 1–4 — табл. 6.1 до 10 м, табл. 6.3 от 10 до 20 м; группы 5–6 — табл. 6.2 по высоте складирования до 5,5 м,
/// при высоте помещения более 10 м (до 30 м) — с множителем примечания 3; S, продолжительность и шаг — табл. 6.1.
/// Вне таблиц (высокостеллажные склады, помещения выше 20/30 м, группа 7 на воде) — ошибка, а не догадка.
/// </summary>
/// <param name="HeightFactor">Множитель примечания 3 к табл. 6.2 (1 — без пересчёта).</param>
/// <param name="Source">Откуда взяты i и Q — для записки.</param>
public sealed record FireParameters(
    string Group,
    double Intensity,
    Flow Flow,
    double MinArea,
    double Duration,
    Length MaxSpacing,
    double HeightFactor,
    string Source,
    Length? RoomHeight,
    Length? StorageHeight,
    IReadOnlyList<string> NormIds)
{
    public static FireParametersResult Resolve(INormsProvider norms, string group, Length? roomHeight, Length? storageHeight)
    {
        ArgumentNullException.ThrowIfNull(norms);
        var issues = new List<Issue>();
        FireParametersResult Fail(string code, string message, string clause)
        {
            issues.Add(new Issue(code, IssueLevel.Error, message, Clause: clause));
            return new FireParametersResult(null, issues);
        }

        var t61 = norms.ByClause("табл. 6.1").FirstOrDefault(r => r.Attributes.GetValueOrDefault("group") == group);
        if (t61 is null)
        {
            return Fail("params.noGroup", $"Группы помещений «{group}» нет в табл. 6.1.", "табл. 6.1");
        }

        double V(NormRecord r, string key) => r.Values.GetValueOrDefault(key) ?? double.NaN;
        var s = V(t61, "S");
        var t = V(t61, "t_min");
        var spacing = new Length(V(t61, "maxSpacing"));
        var h = roomHeight?.Meters;
        var ids = new List<string> { t61.Id };

        if (group is "5" or "6" or "7")
        {
            if (storageHeight is not { } storage)
            {
                return Fail("params.noStorageHeight", $"Для склада (группа {group}) нужна высота складирования — параметры по табл. 6.2.", "табл. 6.2");
            }

            var note = norms.Find("sp485.t6_2.note3")!;
            var bands = norms.ByClause("табл. 6.2").Where(r => r.Attributes.GetValueOrDefault("group") == group && r.Values.ContainsKey("hMax")).ToList();
            var band = bands.FirstOrDefault(r => storage.Meters > V(r, "hMin") + 1e-9 && storage.Meters <= V(r, "hMax") + 1e-9);
            if (band is null)
            {
                return Fail("params.storageTooHigh", $"Высота складирования {storage} больше {new Length(V(note, "storageMax"))}: табл. 6.2 не охватывает высокостеллажные склады — нужны специальные технические условия.", "табл. 6.2");
            }

            if (band.Values.GetValueOrDefault("i_water") is not { } i || band.Values.GetValueOrDefault("Q_water") is not { } q)
            {
                return Fail("params.noWater", $"Группа {group} водой не тушится (табл. 6.2: «-»); пенные установки — вне v1.", "табл. 6.2");
            }

            var factor = 1.0;
            var source = "табл. 6.2";
            if (h is { } room && room > V(note, "hRef") + 1e-9)
            {
                if (room > V(note, "hMax") + 1e-9)
                {
                    return Fail("params.roomTooHigh", $"Высота помещения склада {roomHeight} больше {new Length(V(note, "hMax"))} — вне примечания 3 к табл. 6.2.", "табл. 6.2");
                }

                factor = V(note, "k0") + (V(note, "k1") * (room - V(note, "hRef")));
                source = "табл. 6.2, прим. 3";
                ids.Add(note.Id);
            }

            ids.Add(band.Id);
            return new FireParametersResult(new FireParameters(group, i * factor, new Flow(q * factor), s, t, spacing, factor, source, roomHeight, storageHeight, ids), issues);
        }

        // Группы 1–4: до 10 м — табл. 6.1, от 10 до 20 м — табл. 6.3 (строка «от 10» включает 10 м).
        if (h is { } height && height >= 10 - 1e-9)
        {
            var rows = norms.ByClause("табл. 6.3").Where(r => r.Attributes.GetValueOrDefault("group") == group).ToList();
            var row = rows.FirstOrDefault(r =>
                (r.Attributes.GetValueOrDefault("minInclusive") == "true" ? height >= V(r, "Hmin") - 1e-9 : height > V(r, "Hmin") + 1e-9)
                && height <= V(r, "Hmax") + 1e-9);
            if (row is null)
            {
                return Fail("params.roomTooHigh", $"Высота помещения {roomHeight} больше 20 м: спринклерные параметры табл. 6.3 не применяются (примечание 3 к табл. 6.1 — дренчерные АУП или АУП-ПП).", "табл. 6.3");
            }

            if (row.Values.GetValueOrDefault("i_water") is not { } i3 || row.Values.GetValueOrDefault("Q_water") is not { } q3)
            {
                return Fail("params.noWater", $"Для группы {group} в табл. 6.3 нет параметров водой.", "табл. 6.3");
            }

            ids.Add(row.Id);
            return new FireParametersResult(new FireParameters(group, i3, new Flow(q3), V(row, "S"), t, spacing, 1, "табл. 6.3", roomHeight, null, ids), issues);
        }

        if (t61.Values.GetValueOrDefault("i_water") is not { } i1 || t61.Values.GetValueOrDefault("Q_water") is not { } q1)
        {
            return Fail("params.noWater", $"Для группы {group} в табл. 6.1 нет параметров водой.", "табл. 6.1");
        }

        return new FireParametersResult(new FireParameters(group, i1, new Flow(q1), s, t, spacing, 1, "табл. 6.1", roomHeight, null, ids), issues);
    }
}
