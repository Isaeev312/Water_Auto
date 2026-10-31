using WaterAuto.Calc.Routing;
using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

/// <summary>Труба обвязки насоса и скорость воды в ней при подаче насоса, м/с.</summary>
public sealed record StationPipe(SeriesPipe Pipe, double Velocity);

/// <summary>
/// Насосная установка: рабочие и резервные агрегаты (п. 6.10.3), трубы обвязки одного агрегата по скорости (п. 6.7.1.37),
/// патрубки для мобильной пожарной техники (п. 6.10.17). Марка насоса не подбирается — нужен каталог.
/// </summary>
public sealed record PumpStationResult(
    Flow PumpFlow,
    Length PumpHead,
    int WorkingPumps,
    int StandbyPumps,
    StationPipe Suction,
    double SuctionLimit,
    StationPipe Discharge,
    double DischargeLimit,
    int TruckInlets,
    int TruckInletDn,
    IReadOnlyList<string> NormIds,
    IReadOnlyList<Issue> Issues);

public static class PumpStation
{
    /// <param name="inletPressure">Давление на входе насоса: больше нуля — всасывание с подпором (предел скорости для положительного давления).</param>
    public static PumpStationResult Design(INormsProvider norms, Flow pumpFlow, Length pumpHead, PipeSeries series, Pressure inletPressure)
    {
        ArgumentNullException.ThrowIfNull(norms);
        ArgumentNullException.ThrowIfNull(series);
        var standby = Need(norms, "sp485.p6_10_3.standby");
        var velocity = Need(norms, "sp485.p6_7_1_37.velocity");
        var truck = Need(norms, "sp485.p6_10_17.truckInlets");
        var issues = new List<Issue>();

        // Один рабочий агрегат на полную подачу; резервный — такой же (п. 6.10.3).
        var suctionLimit = inletPressure.Mpa > 0 ? velocity.Values["suctionPositiveMax"]!.Value : velocity.Values["suctionNegativeMax"]!.Value;
        var dischargeLimit = velocity.Values["pressureMax"]!.Value;
        var q = pumpFlow.LitersPerSecond / 1000;
        StationPipe Pipe(double vMax, string side)
        {
            // Внутренний диаметр не меньше √(4Q/(π·v)).
            var inner = Math.Sqrt(4 * q / (Math.PI * vMax)) * 1000;
            var pipe = series.WithInnerAtLeast(inner, 0);
            if (pipe is null)
            {
                issues.Add(new Issue("pump.pipeTooLarge", IssueLevel.Error, $"Для {side} трубопровода насоса нужен внутренний диаметр не меньше {inner:0} мм — в сортаменте такой трубы нет.", Clause: "6.7.1.37"));
                return new StationPipe(new SeriesPipe(0, inner, 0, default, "", ""), 0);
            }

            var d = (pipe.OuterDiameterMm - (2 * pipe.WallMm)) / 1000;
            return new StationPipe(pipe, q / (Math.PI * d * d / 4));
        }

        var suction = Pipe(suctionLimit, "всасывающего");
        var discharge = Pipe(dischargeLimit, "напорного");
        issues.AddRange(series.Issues);
        return new PumpStationResult(
            pumpFlow,
            pumpHead,
            1,
            (int)standby.Values["minStandby"]!.Value,
            suction,
            suctionLimit,
            discharge,
            dischargeLimit,
            (int)truck.Values["minCount"]!.Value,
            (int)truck.Values["dn"]!.Value,
            [standby.Id, velocity.Id, truck.Id],
            issues);
    }

    private static NormRecord Need(INormsProvider norms, string id) =>
        norms.Find(id) ?? throw new InvalidOperationException($"В данных норм нет записи {id}.");
}
