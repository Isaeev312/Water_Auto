using WaterAuto.Calc.Powder;
using WaterAuto.Core;

namespace WaterAuto.Calc.Tests;

// Расстановка модулей сеткой: не меньше расчётного N, ячейка не больше стороны квадрата защищаемой площади модуля.
public class PowderLayoutTests
{
    private static Polygon Rect(double w, double h) => new([new(0, 0), new(w, 0), new(w, h), new(0, h)]);

    [Fact]
    public void Grid_has_at_least_N_modules_and_cells_fit_the_module_square()
    {
        // 30 × 20 м, N = 24, Sн = 26 м² (сторона 5,1 м): 6 × 4 ячеек по 5 × 5 м.
        var points = PowderLayout.Place(Rect(30000, 20000), 24, 26);

        points.Count.ShouldBe(24);
        points.Select(p => p.XMm).Distinct().Count().ShouldBe(6);
        points.Select(p => p.YMm).Distinct().Count().ShouldBe(4);
        points.ShouldContain(p => Math.Abs(p.XMm - 2500) < 1 && Math.Abs(p.YMm - 2500) < 1);
    }

    [Fact]
    public void Small_N_in_a_long_room_is_raised_to_cover_the_room()
    {
        // 30 × 4 м, N = 2, Sн = 26 м²: две ячейки 15 × 4 больше стороны 5,1 м — нужно 6 ячеек по 5 × 4 м.
        var points = PowderLayout.Place(Rect(30000, 4000), 2, 26);

        points.Count.ShouldBe(6);
    }
}
