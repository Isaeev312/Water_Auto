using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

/// <summary>
/// Набор норм ВПВ страны объекта (ADR 0014): документ, таблицы и ID записей в data/norms. Чисел здесь нет — только ссылки на данные.
/// Расчёт по наборам один и тот же (таблицы СН КР 40-04:2025 повторяют по устройству табл. 7.1–7.3 СП 10.13130.2020).
/// </summary>
/// <param name="Prefix">Префикс ID записей набора: таблицы ищутся только среди них.</param>
/// <param name="Table1">Жилые и общественные здания.</param>
/// <param name="Table2">Производственные и складские здания.</param>
/// <param name="Table3">Расход ствола и напор (давление) у диктующего ПК.</param>
/// <param name="HeadInMpa">В таблице 3 давление в МПа (колонки P_…), иначе напор в м (колонки H_…).</param>
/// <param name="ExtraJetId">Дополнительная струя для залов с массовым пребыванием; null — в документе нет.</param>
/// <param name="ValveHeightId">Высота клапана ПК над полом; null — в документе нет (в СП 10 п. 6.2.5 исключён Изм. № 1).</param>
/// <param name="ValveDnId">Граница расхода для клапана DN50/DN65; null — в документе нет.</param>
/// <param name="HighRiseId">Особое правило для высоких производственных зданий; null — в документе нет.</param>
/// <param name="ParkingSupported">Струи для автостоянок по п. 6.4 СНиП 21-02-99* (только для КР, ADR 0010).</param>
public sealed record VpvNorms(
    NormsRegion Region,
    string Doc,
    string Prefix,
    string Table1,
    string Table2,
    string Table3,
    bool HeadInMpa,
    string JetHeightId,
    string JetHeightClause,
    string DiaphragmId,
    string DiaphragmClause,
    string? ValveHeightId,
    string ValveHeightClause,
    string PlacementClause,
    string DurationClause,
    string? ExtraJetId,
    string? ValveDnId,
    string? HighRiseId,
    bool ParkingSupported)
{
    public static readonly VpvNorms Kg = new(
        NormsRegion.Kg, "СН КР 40-04:2025", "snkr4004.", "табл. Ж.1", "табл. Ж.2", "табл. Ж.3", false,
        "snkr4004.p7_9.jetHeight", "7.9",
        "snkr4004.p7_10_11.pressure", "примечание к п. 7.11",
        "snkr4004.p7_16.valveHeight", "7.16",
        "7.14", "7.13",
        "snkr4004.p7_7.extraJet", "snkr4004.p7_9.valveDn", null, true);

    public static readonly VpvNorms Ru = new(
        NormsRegion.Ru, "СП 10.13130.2020", "sp10.", "табл. 7.1", "табл. 7.2", "табл. 7.3", true,
        "sp10.p7_15.jetHeight", "7.15",
        "sp10.p7_5.pressure", "7.5",
        null, "",
        "6.2.2", "6.1.23",
        null, null, "sp10.p7_13.highRise", false);

    public static VpvNorms For(NormsRegion region) => region == NormsRegion.Ru ? Ru : Kg;
}
