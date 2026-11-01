using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

/// <summary>
/// Данные проекта для ВПВ (СН КР 40-04:2025, ADR 0010): признаки здания для табл. Ж.2, оборудование ПК для табл. Ж.3
/// и трубопровод от узла ввода трассы до диктующего ПК.
/// </summary>
/// <param name="FireResistance">Степень огнестойкости здания: I, II, III, IV, V.</param>
/// <param name="Category">Категория здания по пожарной опасности: А, Б, В, Г, Д.</param>
/// <param name="HazardClass">Класс конструктивной пожарной опасности здания С0–С3 (не класс К0–К3 из раскладки).</param>
/// <param name="AboveAxis">Отметка клапана диктующего ПК над осью насоса.</param>
/// <param name="Zh1Row">Строка табл. Ж.1 (жилые и общественные здания) — ID без префикса, напр. «g4.seats_over300»; null — табл. Ж.2.</param>
/// <param name="ExtraJet">Зал с массовым пребыванием людей при сгораемой отделке — на одну струю больше (п. 7.7, только с табл. Ж.1).</param>
/// <param name="Region">Страна объекта (ADR 0014): КР — СН КР 40-04:2025; РФ — СП 10.13130.2020.</param>
/// <param name="IsParking">Отапливаемая автостоянка закрытого типа: струи — по п. 6.4 СНиП 21-02-99* вместо табл. Ж.2 (ADR 0010).</param>
public sealed record HydrantSettings(
    string FireResistance,
    string Category,
    string HazardClass,
    double VolumeThousandM3,
    Length BuildingHeight,
    Length RoomHeight,
    int ValveDn,
    int NozzleMm,
    int HoseLength,
    Length AboveAxis,
    Length PipeLength,
    int PipeDn,
    bool IsParking = false,
    string? Zh1Row = null,
    bool ExtraJet = false,
    NormsRegion Region = NormsRegion.Kg);

/// <summary>Требование к ВПВ: n струй, расход и напор диктующего ПК по табл. Ж.3.</summary>
public sealed record HydrantDemandValue(
    int Jets,
    Flow MinJetFlow,
    Length RequiredJetHeight,
    Length TableJetHeight,
    Flow JetFlow,
    Length Head,
    IReadOnlyList<string> NormIds,
    string Source = "табл. Ж.2 СН КР 40-04:2025");

public sealed record HydrantDemandResult(HydrantDemandValue? Demand, IReadOnlyList<Issue> Issues);

/// <summary>
/// Число струй и минимальный расход — табл. Ж.2; высота компактной струи — не меньше высоты помещения и минимума п. 7.9;
/// для автостоянок — п. 6.4 СНиП 21-02-99*; расход ствола и напор у диктующего ПК — табл. Ж.3, первая строка с высотой струи
/// не меньше требуемой и расходом ствола не меньше минимального (без интерполяции).
/// </summary>
public static class HydrantDemand
{
    // Латинские буквы, похожие на кириллические (A, B, C, E…), — к кириллице: в таблицах Ж.2 категории и классы кириллицей.
    private static string Cyrillic(string v) => new(v.Trim().ToUpperInvariant().Select(c => c switch
    {
        'A' => 'А',
        'B' => 'В',
        'C' => 'С',
        'E' => 'Е',
        _ => c,
    }).ToArray());

    public static HydrantDemandResult Resolve(INormsProvider norms, HydrantSettings s)
    {
        ArgumentNullException.ThrowIfNull(norms);
        ArgumentNullException.ThrowIfNull(s);
        s = s with { Category = Cyrillic(s.Category), HazardClass = Cyrillic(s.HazardClass), FireResistance = s.FireResistance.Trim().ToUpperInvariant() };
        var issues = new List<Issue>();
        HydrantDemandResult Stop(string code, IssueLevel level, string message, string clause)
        {
            issues.Add(new Issue(code, level, message, Clause: clause));
            return new HydrantDemandResult(null, issues);
        }

        var set = VpvNorms.For(s.Region);
        IEnumerable<NormRecord> Table(string clause) => norms.ByClause(clause).Where(r => r.Id.StartsWith(set.Prefix, StringComparison.Ordinal));
        if (s.IsParking && !set.ParkingSupported)
        {
            return Stop("vpv.noNormsRu", IssueLevel.Error,
                $"Струи ВПВ для автостоянки в РФ — по СП 113.13330, его данных нет в программе; п. 6.4 СНиП 21-02-99* применён только для КР (ADR 0010). Расчёт ВПВ не выполнен — задайте здание по {set.Table2}.",
                "СП 113.13330");
        }

        int n;
        double qMin;
        string rowId, source;
        if (s.Zh1Row is { } zh1 && !s.IsParking)
        {
            var row = Table(set.Table1).FirstOrDefault(r => r.Id.EndsWith($".{zh1}", StringComparison.Ordinal));
            if (row is null)
            {
                return Stop("vpv.noRow", IssueLevel.Error, $"В {set.Table1} {set.Doc} нет строки «{zh1}».", set.Table1);
            }

            n = (int)row.Values["jets"]!.Value;
            qMin = row.Values["jetFlow"]!.Value;
            (rowId, source) = (row.Id, $"{set.Table1} {set.Doc} ({row.Attributes["description"]})");
        }
        else if (s.IsParking)
        {
            var p = norms.Find("snip2102.p6_4.jets")!;
            if (s.VolumeThousandM3 < p.Values["volumeMin"]!.Value)
            {
                return Stop("vpv.notRequired", IssueLevel.Info, $"Объём пожарного отсека меньше {p.Values["volumeMin"]} тыс. м³ — ВПВ по п. 6.4 СНиП 21-02-99* не требуется.", "6.4");
            }

            n = (int)p.Values["jets"]!.Value;
            qMin = p.Values[s.VolumeThousandM3 <= p.Values["volumeSplit"]!.Value ? "jetFlowUpTo5" : "jetFlowOver5"]!.Value;
            (rowId, source) = (p.Id, "п. 6.4 СНиП 21-02-99*");
        }
        else
        {
            static IEnumerable<string> Split(string v) => v.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Where(x => x != "и");
            var row = Table(set.Table2).FirstOrDefault(r =>
                Split(r.Attributes["fireResistance"]).Contains(s.FireResistance)
                && Split(r.Attributes["category"]).Contains(s.Category)
                && (r.Attributes["constructionClass"] == "не нормируется" || Split(r.Attributes["constructionClass"]).Contains(s.HazardClass)));
            if (row is null)
            {
                return Stop("vpv.noRow", IssueLevel.Error, $"В {set.Table2} нет строки для степени {s.FireResistance}, категории {s.Category}, класса {s.HazardClass}. Допустимо: степень I–V, категория А, Б, В, Г, Д, класс С0–С3.", set.Table2);
            }

            if (row.Values.GetValueOrDefault("heightMax") is { } heightMax && s.BuildingHeight.Meters > heightMax + 1e-9)
            {
                return Stop("vpv.outOfTable", IssueLevel.Error, $"Высота здания {s.BuildingHeight} больше {heightMax} м — вне {set.Table2}.", set.Table2);
            }

            if (s.VolumeThousandM3 < row.Values["volumeMin"]!.Value)
            {
                return Stop("vpv.notRequired", IssueLevel.Info, $"Объём здания меньше {row.Values["volumeMin"]} тыс. м³ — ВПВ по {set.Table2} не требуется.", set.Table2);
            }

            var small = s.VolumeThousandM3 <= row.Values["volumeSplit"]!.Value;
            var jets = row.Values[small ? "jetsUpTo150" : "jetsOver150"];
            var minFlow = row.Values[small ? "jetFlowUpTo150" : "jetFlowOver150"];
            if (jets is not { } jetsValue || minFlow is not { } flowMin)
            {
                return Stop("vpv.notRequired", IssueLevel.Info, $"ВПВ по {set.Table2} не требуется.", set.Table2);
            }

            n = (int)jetsValue;
            qMin = flowMin;
            (rowId, source) = (row.Id, $"{set.Table2} {set.Doc}");
            if (set.HighRiseId is { } highId && norms.Find(highId) is { } high
                && s.BuildingHeight.Meters > high.Values["heightOver"]!.Value + 1e-9 && s.VolumeThousandM3 > high.Values["volumeOver"]!.Value + 1e-9)
            {
                // Высокое производственное здание большого объёма — особое правило вместо строки таблицы.
                n = (int)high.Values["jets"]!.Value;
                qMin = high.Values["jetFlow"]!.Value;
                (rowId, source) = (high.Id, $"п. {high.Clause} {set.Doc}");
            }
        }

        string? extraId = null;
        if (s.ExtraJet)
        {
            if (set.ExtraJetId is null)
            {
                issues.Add(new Issue("vpv.extraJet", IssueLevel.Warning, $"В {set.Doc} нет правила о дополнительной струе для залов — признак не учтён.", Clause: set.Table1));
            }
            else if (s.Zh1Row is null || s.IsParking)
            {
                issues.Add(new Issue("vpv.extraJet", IssueLevel.Warning, "Дополнительная струя по п. 7.7 относится к табл. Ж.1 — признак не учтён.", Clause: "7.7"));
            }
            else
            {
                var extra = norms.Find(set.ExtraJetId)!;
                n += (int)extra.Values["jets"]!.Value;
                extraId = extra.Id;
                source += ", п. 7.7";
            }
        }

        var heightRecord = norms.Find(set.JetHeightId)!;
        var minHeight = s.BuildingHeight.Meters <= heightRecord.Values["buildingHeightLimit"]!.Value
            ? heightRecord.Values["minUpTo50"]!.Value
            : heightRecord.Values["otherOver50"]!.Value;
        var required = Math.Max(s.RoomHeight.Meters, minHeight);
        var dn = s.ValveDn.ToString(CultureInfo.InvariantCulture);
        var qKey = string.Create(CultureInfo.InvariantCulture, $"q_{s.NozzleMm}");
        var hKey = string.Create(CultureInfo.InvariantCulture, $"{(set.HeadInMpa ? "P" : "H")}_{s.NozzleMm}_{s.HoseLength}");
        var rows = Table(set.Table3)
            .Where(r => r.Attributes["valveDn"] == dn && r.Values["jetHeight"]!.Value >= required - 1e-9)
            .OrderBy(r => r.Values["jetHeight"]!.Value)
            .ToList();
        if (rows.Count == 0)
        {
            return Stop("vpv.outOfTable", IssueLevel.Error, $"В {set.Table3} для клапана DN{s.ValveDn} нет высоты струи {required:0.##} м.", set.Table3);
        }

        if (rows[0].Values.GetValueOrDefault(qKey) is null || rows[0].Values.GetValueOrDefault(hKey) is null)
        {
            return Stop("vpv.noTableValue", IssueLevel.Error, $"{set.Table3}: для DN{s.ValveDn}, спрыска {s.NozzleMm} мм, рукава {s.HoseLength} м при струе {rows[0].Values["jetHeight"]} м значения нет.", set.Table3);
        }

        // Расход меньше минимального — берётся более длинная компактная струя той же колонки (больший напор).
        var t3 = rows.FirstOrDefault(r => r.Values.GetValueOrDefault(qKey) is { } rq && rq >= qMin - 1e-9 && r.Values.GetValueOrDefault(hKey) is not null);
        if (t3 is null)
        {
            return Stop("vpv.jetFlowLow", IssueLevel.Error, $"Ствол {s.NozzleMm} мм при клапане DN{s.ValveDn} не даёт {qMin} л/с ({source}) ни при какой высоте струи {set.Table3} — взять больший спрыск или клапан.", set.Table3);
        }

        var q = t3.Values[qKey];
        var h = set.HeadInMpa ? new Pressure(t3.Values[hKey]!.Value).ToHeight().Meters : t3.Values[hKey];

        var valve = set.ValveDnId is { } valveId ? norms.Find(valveId)! : null;
        if (valve is not null && q!.Value > valve.Values["dn50MaxFlow"]!.Value + 1e-9 && s.ValveDn == (int)valve.Values["dnSmall"]!.Value)
        {
            issues.Add(new Issue("vpv.valveDn", IssueLevel.Warning, $"Расход струи {q} л/с больше {valve.Values["dn50MaxFlow"]} л/с: по примечанию к 7.9 нужен клапан DN{valve.Values["dnLarge"]} (DN50 — при ТЭО).", Clause: "7.9"));
        }

        return new HydrantDemandResult(
            new HydrantDemandValue(n, new Flow(qMin), new Length(required), new Length(t3.Values["jetHeight"]!.Value), new Flow(q!.Value), new Length(h!.Value), [rowId, .. extraId is null ? Array.Empty<string>() : [extraId], heightRecord.Id, t3.Id, .. valve is null ? Array.Empty<string>() : [valve.Id]], source),
            issues);
    }
}
