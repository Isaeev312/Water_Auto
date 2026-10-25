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
    int PipeDn);

/// <summary>Требование к ВПВ: n струй, расход и напор диктующего ПК по табл. Ж.3.</summary>
public sealed record HydrantDemandValue(
    int Jets,
    Flow MinJetFlow,
    Length RequiredJetHeight,
    Length TableJetHeight,
    Flow JetFlow,
    Length Head,
    IReadOnlyList<string> NormIds);

public sealed record HydrantDemandResult(HydrantDemandValue? Demand, IReadOnlyList<Issue> Issues);

/// <summary>
/// Число струй и минимальный расход — табл. Ж.2; высота компактной струи — не меньше высоты помещения и минимума п. 7.9;
/// расход ствола и напор у диктующего ПК — табл. Ж.3, строка с высотой струи не меньше требуемой (без интерполяции).
/// </summary>
public static class HydrantDemand
{
    public static HydrantDemandResult Resolve(INormsProvider norms, HydrantSettings s)
    {
        ArgumentNullException.ThrowIfNull(norms);
        ArgumentNullException.ThrowIfNull(s);
        var issues = new List<Issue>();
        HydrantDemandResult Stop(string code, IssueLevel level, string message, string clause)
        {
            issues.Add(new Issue(code, level, message, Clause: clause));
            return new HydrantDemandResult(null, issues);
        }

        static IEnumerable<string> Split(string v) => v.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Where(x => x != "и");
        var row = norms.ByClause("табл. Ж.2").FirstOrDefault(r =>
            Split(r.Attributes["fireResistance"]).Contains(s.FireResistance)
            && Split(r.Attributes["category"]).Contains(s.Category)
            && (r.Attributes["constructionClass"] == "не нормируется" || Split(r.Attributes["constructionClass"]).Contains(s.HazardClass)));
        if (row is null)
        {
            return Stop("vpv.noRow", IssueLevel.Error, $"В табл. Ж.2 нет строки для степени {s.FireResistance}, категории {s.Category}, класса {s.HazardClass}.", "табл. Ж.2");
        }

        if (s.BuildingHeight.Meters > row.Values["heightMax"]!.Value + 1e-9)
        {
            return Stop("vpv.outOfTable", IssueLevel.Error, $"Высота здания {s.BuildingHeight} больше {row.Values["heightMax"]} м — вне табл. Ж.2.", "табл. Ж.2");
        }

        if (s.VolumeThousandM3 < row.Values["volumeMin"]!.Value)
        {
            return Stop("vpv.notRequired", IssueLevel.Info, $"Объём здания меньше {row.Values["volumeMin"]} тыс. м³ — ВПВ по табл. Ж.2 не требуется.", "табл. Ж.2");
        }

        var small = s.VolumeThousandM3 <= row.Values["volumeSplit"]!.Value;
        var jets = row.Values[small ? "jetsUpTo150" : "jetsOver150"];
        var minFlow = row.Values[small ? "jetFlowUpTo150" : "jetFlowOver150"];
        if (jets is not { } n || minFlow is not { } qMin)
        {
            return Stop("vpv.notRequired", IssueLevel.Info, "ВПВ по табл. Ж.2 не требуется.", "табл. Ж.2");
        }

        var heightRecord = norms.Find("snkr4004.p7_9.jetHeight")!;
        var minHeight = s.BuildingHeight.Meters <= heightRecord.Values["buildingHeightLimit"]!.Value
            ? heightRecord.Values["minUpTo50"]!.Value
            : heightRecord.Values["otherOver50"]!.Value;
        var required = Math.Max(s.RoomHeight.Meters, minHeight);
        var dn = s.ValveDn.ToString(CultureInfo.InvariantCulture);
        var t3 = norms.ByClause("табл. Ж.3")
            .Where(r => r.Attributes["valveDn"] == dn && r.Values["jetHeight"]!.Value >= required - 1e-9)
            .MinBy(r => r.Values["jetHeight"]!.Value);
        if (t3 is null)
        {
            return Stop("vpv.outOfTable", IssueLevel.Error, $"В табл. Ж.3 для клапана DN{s.ValveDn} нет высоты струи {required:0.##} м.", "табл. Ж.3");
        }

        var q = t3.Values.GetValueOrDefault(string.Create(CultureInfo.InvariantCulture, $"q_{s.NozzleMm}"));
        var h = t3.Values.GetValueOrDefault(string.Create(CultureInfo.InvariantCulture, $"H_{s.NozzleMm}_{s.HoseLength}"));
        if (q is null || h is null)
        {
            return Stop("vpv.noTableValue", IssueLevel.Error, $"Табл. Ж.3: для DN{s.ValveDn}, спрыска {s.NozzleMm} мм, рукава {s.HoseLength} м при струе {t3.Values["jetHeight"]} м значения нет.", "табл. Ж.3");
        }

        if (q.Value < qMin - 1e-9)
        {
            return Stop("vpv.jetFlowLow", IssueLevel.Error, $"Расход ствола {q} л/с меньше {qMin} л/с по табл. Ж.2 — взять больший спрыск или клапан.", "табл. Ж.2");
        }

        var valve = norms.Find("snkr4004.p7_9.valveDn")!;
        if (q.Value > valve.Values["dn50MaxFlow"]!.Value + 1e-9 && s.ValveDn == (int)valve.Values["dnSmall"]!.Value)
        {
            issues.Add(new Issue("vpv.valveDn", IssueLevel.Warning, $"Расход струи {q} л/с больше {valve.Values["dn50MaxFlow"]} л/с: по примечанию к 7.9 нужен клапан DN{valve.Values["dnLarge"]} (DN50 — при ТЭО).", Clause: "7.9"));
        }

        return new HydrantDemandResult(
            new HydrantDemandValue((int)n, new Flow(qMin), new Length(required), new Length(t3.Values["jetHeight"]!.Value), new Flow(q.Value), new Length(h.Value), [row.Id, heightRecord.Id, t3.Id, valve.Id]),
            issues);
    }
}
