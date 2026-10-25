using WaterAuto.Core;

namespace WaterAuto.Import.Tests;

internal sealed class PlanPointComparer(double toleranceMm) : IEqualityComparer<PlanPoint>
{
    public bool Equals(PlanPoint a, PlanPoint b) =>
        Math.Abs(a.XMm - b.XMm) <= toleranceMm && Math.Abs(a.YMm - b.YMm) <= toleranceMm;

    public int GetHashCode(PlanPoint p) => 0;
}
