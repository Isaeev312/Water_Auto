using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

public class FormulaTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    [Fact]
    public void Emitter_flow_is_10_K_sqrt_P()
    {
        // ГидРаВПТ R2, л. 9: K = 0,47, P = 0,188 → q = 2,038 л/с.
        TreeSolver.EmitterFlow(new KFactor(0.47), new Pressure(0.188)).LitersPerSecond.ShouldBe(2.0379, 1e-4);
    }

    [Fact]
    public void Emitter_flow_is_zero_without_pressure()
    {
        TreeSolver.EmitterFlow(new KFactor(0.47), new Pressure(-0.01)).ShouldBe(Flow.Zero);
    }

    [Fact]
    public void Pipe_loss_is_Q2_L_over_100_Kt()
    {
        // ГидРаВПТ R2 из файла .hvd (без множителя): ветвь 1, участок 1-2 — 0,008536 МПа.
        var pipe = new NetworkPipe("1-2", "a", "b", new Length(2.31), new PipeCharacteristic(11.238334));
        var q = 2.0379;
        (TreeSolver.Resistance(pipe) * q * q).ShouldBe(0.008536, 1e-6);
    }

    [Fact]
    public void Kt_comes_from_table_B2_when_the_row_exists()
    {
        var kt = PipeCharacteristics.Resolve(Norms, "ГОСТ 3262", 32, Length.FromMillimeters(42.3), Length.FromMillimeters(2.8))!;
        kt.Source.ShouldBe(CharacteristicSource.TableB2);
        kt.Value.Kt.ShouldBe(16.5);
    }

    [Fact]
    public void Kt_comes_from_formula_B8_by_inner_diameter_otherwise()
    {
        // ВГП 42,3×4,0 нет в табл. Б.2; ГидРаВПТ даёт 11,238334 (Ø 34,3 мм, ψ = 5,363, θ = 5,42).
        var kt = PipeCharacteristics.Resolve(Norms, "ГОСТ 3262", 32, Length.FromMillimeters(42.3), Length.FromMillimeters(4.0))!;
        kt.Source.ShouldBe(CharacteristicSource.FormulaB8);
        kt.Value.Kt.ShouldBe(11.238334, 1e-6);
    }

    [Fact]
    public void Kt_is_null_for_unknown_standard()
    {
        PipeCharacteristics.Resolve(Norms, "PPR", 32, Length.FromMillimeters(40), Length.FromMillimeters(3.7)).ShouldBeNull();
    }

    [Fact]
    public void Formula_B8_is_not_used_outside_its_DN_range()
    {
        // Табл. Б.3 — для DN 20–200; ВГП DN15 с нестандартной стенкой формулой не считается.
        PipeCharacteristics.Resolve(Norms, "ГОСТ 3262", 15, Length.FromMillimeters(21.3), Length.FromMillimeters(3.2)).ShouldBeNull();
    }

    [Fact]
    public void Formula_B8_rejects_invalid_dimensions_and_results()
    {
        PipeCharacteristics.Resolve(Norms, "ГОСТ 3262", 32, Length.FromMillimeters(42.3), Length.FromMillimeters(22)).ShouldBeNull();
        PipeCharacteristics.Resolve(Norms, "ГОСТ 3262", 32, Length.FromMillimeters(42.3), Length.FromMillimeters(-2.8)).ShouldBeNull();
        PipeCharacteristics.Resolve(Norms, "ГОСТ 3262", 32, Length.FromMillimeters(double.NaN), Length.FromMillimeters(2.8)).ShouldBeNull();
        PipeCharacteristics.Resolve(Norms, "ГОСТ 3262", 32, Length.FromMillimeters(42.3), Length.FromMillimeters(double.PositiveInfinity)).ShouldBeNull();
        PipeCharacteristics.Resolve(Norms, "ГОСТ 3262", 32, Length.FromMillimeters(1e60), Length.FromMillimeters(1)).ShouldBeNull();
        PipeCharacteristics.Resolve(Norms, "ГОСТ 3262", 32, Length.FromMillimeters(1e-100), Length.FromMillimeters(1e-101)).ShouldBeNull();
    }

    [Fact]
    public void Norm_record_preserves_file_edition()
    {
        Norms.Find("sp485.tB_3.Kt.gost3262")!.Edition.ShouldBe("с изм. № 1 (введено с 01.01.2026) и № 2 (введено с 09.07.2026)");
    }

    [Fact]
    public void Table_B2_rows_marked_for_outdoor_networks_are_flagged()
    {
        // 159×4 отмечена «*» — трубы наружного водоснабжения; 159×3,2 — нет.
        PipeCharacteristics.Resolve(Norms, "ГОСТ 10704", 150, Length.FromMillimeters(159), Length.FromMillimeters(4.0))!.OutdoorOnly.ShouldBeTrue();
        PipeCharacteristics.Resolve(Norms, "ГОСТ 10704", 150, Length.FromMillimeters(159), Length.FromMillimeters(3.2))!.OutdoorOnly.ShouldBeFalse();
        PipeCharacteristics.Resolve(Norms, "ГОСТ 3262", 32, Length.FromMillimeters(42.3), Length.FromMillimeters(4.0))!.OutdoorOnly.ShouldBeFalse();
    }

    [Fact]
    public void Table_B2_records_contain_all_keys_used_by_calc()
    {
        // Ключи NormKeys должны совпадать с таблицей требований tools/ValidateData (T4) и с data/norms:
        // иначе Calc ищет в норме поле, которого там нет.
        var rows = Norms.ByClause(NormKeys.ClauseTableB2).ToList();
        rows.ShouldNotBeEmpty();
        foreach (var row in rows)
        {
            foreach (var key in new[] { NormKeys.Dn, NormKeys.DOut, NormKeys.Wall, NormKeys.Kt })
            {
                row.Values.ContainsKey(key).ShouldBeTrue($"{row.Id}: нет ключа values.{key}");
            }
        }
    }

    [Fact]
    public void Kt_resolves_from_pipe_geometry()
    {
        var geometry = new PipeGeometry("ГОСТ 3262", 32, Length.FromMillimeters(42.3), Length.FromMillimeters(2.8));
        PipeCharacteristics.Resolve(Norms, geometry)!.Value.Kt.ShouldBe(16.5);
    }

    [Fact]
    public void Velocity_formula_uses_cubic_metres_per_second()
    {
        // 10 л/с через Ø 100 мм: 0,01 / (π·0,01/4) = 1,2732 м/с.
        Velocity.Of(new Flow(10), Length.FromMillimeters(100)).MetersPerSecond.ShouldBe(1.27324, 1e-5);
    }
}
