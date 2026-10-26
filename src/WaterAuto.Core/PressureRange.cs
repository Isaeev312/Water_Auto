using System.Globalization;

namespace WaterAuto.Core;

/// <summary>
/// Рабочий диапазон давления оросителя pMin–pMax из каталога производителя (data/catalog/sprinklers.json).
/// Это данные производителя, а не норма: расчёт по ним выдаёт замечание, а не ошибку. Границы входят в диапазон.
/// </summary>
public readonly record struct PressureRange(Pressure Min, Pressure Max)
{
    public bool Contains(Pressure pressure) => pressure.Mpa >= Min.Mpa && pressure.Mpa <= Max.Mpa;

    public override string ToString() =>
        Min.Mpa.ToString("0.######", CultureInfo.InvariantCulture) + "–"
        + Max.Mpa.ToString("0.######", CultureInfo.InvariantCulture) + " МПа";
}
