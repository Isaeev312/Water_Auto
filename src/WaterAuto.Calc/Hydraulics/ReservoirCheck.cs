using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

/// <summary>
/// Резервуар пожарного запаса, заданный пользователем (ARCHITECTURE §5: размеры и мёртвый слой).
/// </summary>
/// <param name="Length">Длина в плане, м.</param>
/// <param name="Width">Ширина в плане, м.</param>
/// <param name="Height">Высота воды, м.</param>
/// <param name="DeadLayer">Мёртвый слой, м — вода ниже него насосом не используется.</param>
public sealed record UserTank(Length Length, Length Width, Length Height, Length DeadLayer)
{
    /// <summary>Полезный объём — выше мёртвого слоя, м³ (п. 6.9.13).</summary>
    public double UsefulVolumeM3 => Length.Meters * Width.Meters * Math.Max(0, Height.Meters - DeadLayer.Meters);
}

/// <summary>Сравнение резервуара пользователя с требуемым запасом воды (этап 5).</summary>
public static class ReservoirCheck
{
    /// <summary>
    /// Заполнение резервуара — не более maxFillPercent % вместимости (п. 6.9.13), поэтому в полезном объёме помещается
    /// полезный_объём × maxFillPercent / 100. Если этого меньше требуемого запаса — замечание; резервуар не задан — без проверки.
    /// </summary>
    /// <param name="tank">Резервуар пользователя; null — сравнение не выполняется.</param>
    /// <param name="requiredWaterM3">Требуемый запас воды W = Q·t·60/1000, м³ (<see cref="DesignResult.WaterVolume"/>).</param>
    /// <param name="maxFillPercent">Максимальное заполнение, % (<see cref="DesignResult.TankMaxFill"/>).</param>
    public static Issue? Check(UserTank? tank, double requiredWaterM3, double maxFillPercent)
    {
        if (tank is null)
        {
            return null;
        }

        var available = tank.UsefulVolumeM3 * maxFillPercent / 100.0;
        if (available + 1e-9 >= requiredWaterM3)
        {
            return null;
        }

        var ru = CultureInfo.GetCultureInfo("ru-RU");
        return new Issue(
            "reservoir.insufficient",
            IssueLevel.Warning,
            string.Create(ru, $"Полезный объём резервуара {tank.UsefulVolumeM3:0.0} м³ × {maxFillPercent:0} % = {available:0.0} м³ меньше требуемого запаса {requiredWaterM3:0.0} м³ — увеличить резервуар."),
            Clause: "6.9.13");
    }
}
