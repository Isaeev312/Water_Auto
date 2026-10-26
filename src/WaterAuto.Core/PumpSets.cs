namespace WaterAuto.Core;

/// <summary>Габариты в плане и по высоте, мм.</summary>
public sealed record Footprint(double LengthMm, double WidthMm, double HeightMm);

/// <summary>
/// Комплектная насосная установка пожаротушения из каталога: основные и резервные насосы, кривая Q–H одного насоса
/// (м³/ч, м), габариты гидравлической части и прибора управления, DN коллектора.
/// </summary>
public sealed record PumpSet(
    string Designation,
    string Pump,
    int MainPumps,
    int StandbyPumps,
    double PowerKw,
    IReadOnlyList<(double QM3h, double HM)> PumpCurve,
    Footprint Hydraulic,
    Footprint Cabinet,
    int CollectorDn,
    string Maker,
    string Source)
{
    /// <summary>Высота оси коллекторов установки от пола, мм.</summary>
    public double CollectorAxisMm { get; init; } = 200;
}
