using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Calc.Gas;

/// <summary>
/// Масса ГОТВ при объёмном тушении по прил. Д СП 485.1311500.2020 и площадь проёма сброса давления по прил. Ж:
/// Mг = K1·[Mр + Mтр + Mб·n] (Д.1); Mр — по Д.2 (сжиженные, кроме CO₂) или Д.3 (сжатые и CO₂); ρ1 = ρ0·T0/Tм·K3 (Д.4);
/// K2 = П·δ·τпод·√H (Д.7), δ = ΣFн/Vр (Д.8); подкласс А1 — Mр = K4·Mр(гептан) (Д.9). Концентрации, плотности, коэффициенты и
/// константы — из данных норм; время подачи без гидравлического расчёта — нормативное по 9.7.4 (А-18).
/// </summary>
public static class GasMass
{
    private const string Doc = "СП 485.1311500.2020";
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static GasMassResult Calculate(GasRoom room, INormsProvider norms)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(norms);
        var issues = new List<Issue>();
        GasMassResult Fail() => new(room.Id, 0, 0, 0, 0, 0, 0, 0, 0, null, issues);
        var agent = norms.Find(room.AgentId);
        var f = norms.Find("sp485.pD_formulas.mass");
        var openings = norms.Find("sp485.pD_2_2.openings");
        var times = norms.Find("sp485.p9_7_4.dischargeTime");
        var a1 = norms.Find("sp485.pD_3.classA1");
        var relief = norms.Find("sp485.pZh_formula.relief");
        if (agent is null || f is null || openings is null || times is null || a1 is null || relief is null)
        {
            issues.Add(new Issue("gas.noNorm", IssueLevel.Error, $"В данных норм нет записей прил. Д, Ж или ГОТВ «{room.AgentId}» (data/norms/sp485)."));
            return Fail();
        }

        var kind = agent.Attributes.GetValueOrDefault("kind") ?? "liquefied";
        var name = agent.Attributes.GetValueOrDefault("agent") ?? room.AgentId;

        // Нормативная концентрация: по материалу; подкласс А1 — от н-гептана с K4 (Д.3).
        var material = room.ClassA1 == GasClassA1.None ? room.Material : "heptane";
        if (agent.Values.GetValueOrDefault(material) is not { } cn)
        {
            issues.Add(new Issue("gas.concentration.missing", IssueLevel.Error,
                $"«{room.Name}»: для {name} в табл. прил. Г нет концентрации по материалу «{room.Material}» — для не-ЛВЖ и подкласса А2 принять н-гептан (прим. 3, 4 к Д.1), иначе МОК по ГОСТ Р 53280.3 × коэффициент безопасности.",
                room.Id, Doc + ", прил. Г, прил. Д"));
            return Fail();
        }

        // Параметр негерметичности δ = ΣFн/Vр — не больше табл. Г.16 (азот — 9.1.3; подкласс А1 — Д.3).
        var delta = room.VolumeM3 > 0 ? room.OpenAreaM2 / room.VolumeM3 : 0;
        var limitRow = Enumerable.Range(1, 30).Select(k => norms.Find($"sp485.tG16.r{k}")).TakeWhile(r => r is not null)
            .FirstOrDefault(r => room.VolumeM3 > r!.Values["volumeFrom"]!.Value - 1e-9 && (r.Values.GetValueOrDefault("volumeTo") is not { } to || room.VolumeM3 <= to + 1e-9));
        var limit = room.AgentId == "sp485.tG.nitrogen" ? norms.Find("sp485.p9_1_3.nitrogenLeakage")?.Values["maxDelta"] : limitRow?.Values["maxDelta"];
        if (limit is { } lim && delta > lim + 1e-12)
        {
            issues.Add(new Issue("gas.leakage", IssueLevel.Error,
                string.Create(Ru, $"«{room.Name}»: параметр негерметичности δ = {delta:0.#####} м⁻¹ больше допустимого {lim:0.####} м⁻¹ для {room.VolumeM3:0} м³ — объёмное тушение недопустимо (9.1.3, табл. Г.16): уменьшить проёмы."),
                room.Id, Doc + ", п. 9.1.3, табл. Г.16"));
        }

        if (room.ClassA1 != GasClassA1.None && delta > a1.Values["maxDelta"]!.Value + 1e-12)
        {
            issues.Add(new Issue("gas.a1.leakage", IssueLevel.Error,
                string.Create(Ru, $"«{room.Name}»: пожар подкласса А1 — δ не более {a1.Values["maxDelta"]:0.###} м⁻¹, а δ = {delta:0.#####} м⁻¹ (Д.3)."), room.Id, Doc + ", прил. Д, п. Д.3"));
        }

        // Время подачи: задано (гидравлический расчёт) или нормативное по 9.7.4 — в запас для K2 и проёма Ж.1.
        var normTime = kind == "liquefied" ? (room.Modular ? times.Values["modularLiquefied"]!.Value : times.Values["centralLiquefied"]!.Value) : times.Values["co2OrCompressed"]!.Value;
        var tau = room.DischargeTimeS ?? normTime;
        if (room.DischargeTimeS is null)
        {
            issues.Add(new Issue("gas.dischargeTime", IssueLevel.Info,
                string.Create(Ru, $"«{room.Name}»: время подачи принято нормативным {normTime:0} с (9.7.4) — уточнить по гидравлическому расчёту изготовителя (А-18)."), room.Id, Doc + ", п. 9.7.4"));
        }
        else if (room.DischargeTimeS > normTime + 1e-9)
        {
            issues.Add(new Issue("gas.dischargeTime.long", IssueLevel.Warning,
                string.Create(Ru, $"«{room.Name}»: время подачи {tau:0.#} с больше нормативного {normTime:0} с (9.7.4)."), room.Id, Doc + ", п. 9.7.4"));
        }

        // K3 — табл. Г.17 по высоте над уровнем моря; ρ1 = ρ0·T0/Tм·K3.
        var k3Row = Enumerable.Range(1, 30).Select(k => norms.Find($"sp485.tG17.r{k}")).TakeWhile(r => r is not null)
            .FirstOrDefault(r => (room.AltitudeM > r!.Values["altitudeFrom"]!.Value - 1e-9 || r.Values["altitudeFrom"] == 0) && (r.Values.GetValueOrDefault("altitudeTo") is not { } to || room.AltitudeM <= to + 1e-9));
        var k3 = k3Row?.Values["k3"] ?? 1;
        var tm = room.MinTemperatureC + f.Values["kelvinOffset"]!.Value;
        var rho1 = agent.Values["density"]!.Value * f.Values["t0"]!.Value / tm * k3;

        // K2 = П·δ·τпод·√H.
        var p = room.Openings switch
        {
            GasOpenings.BothZones => openings.Values["bothZones"]!.Value,
            GasOpenings.TopOnly => openings.Values["topOnly"]!.Value,
            GasOpenings.BottomOnly => openings.Values["bottomOnly"]!.Value,
            _ => openings.Values["other"]!.Value,
        };
        var k2 = p * delta * tau * Math.Sqrt(room.HeightM);

        var b = f.Values["concentrationBase"]!.Value;
        var mp = kind == "liquefied"
            ? room.VolumeM3 * rho1 * (1 + k2) * cn / (b - cn)
            : room.VolumeM3 * rho1 * (1 + k2) * Math.Log(b / (b - cn));
        if (room.ClassA1 != GasClassA1.None)
        {
            var k4 = room.ClassA1 switch
            {
                GasClassA1.PaperBales => a1.Values["paperBales"]!.Value,
                GasClassA1.PaperNoAccess => a1.Values["paperNoAccess"]!.Value,
                _ => a1.Values["otherA1"]!.Value,
            };
            mp *= k4;
            issues.Add(new Issue("gas.a1.sealed", IssueLevel.Info,
                string.Create(Ru, $"«{room.Name}»: подкласс А1 — Mр = K4·Mр(гептан), K4 = {k4:0.##}; в проекте указать: не вскрывать помещение {a1.Values["minuteSealed"]:0} мин после срабатывания (Д.3)."), room.Id, Doc + ", прил. Д, п. Д.3"));
        }

        if (room.PipeResidueKg is null)
        {
            issues.Add(new Issue("gas.pipeResidue", IssueLevel.Warning,
                $"«{room.Name}»: остаток ГОТВ в трубопроводах Mтр (Д.5, Д.6) не учтён — нужен гидравлический расчёт изготовителя; масса предварительная (А-18).", room.Id, Doc + ", прил. Д"));
        }

        var k1 = f.Values["k1"]!.Value;
        var total = k1 * (mp + (room.PipeResidueKg ?? 0) + (room.ModuleResidueKg * room.Modules));

        // Ж.1: проём сброса давления — при заданном предельном давлении; правая часть ≤ 0 — не нужен.
        double? reliefArea = null;
        if (room.MaxOverpressureMPa is { } ppr)
        {
            var pa = f.Values["atmosphere"]!.Value;
            var kPress = room.AgentId switch
            {
                "sp485.tG.nitrogen" => relief.Values["nitrogen"]!.Value,
                "sp485.tG.argon" => relief.Values["argon"]!.Value,
                "sp485.tG.inergen" => relief.Values["inergen"]!.Value,
                _ when kind == "liquefied" || kind == "co2" => relief.Values["liquefied"]!.Value,
                _ => double.NaN,
            };
            if (double.IsNaN(kPress))
            {
                issues.Add(new Issue("gas.relief.noCoefficient", IssueLevel.Error, $"«{room.Name}»: для {name} в прил. Ж нет коэффициента изменения давления — проём сброса не рассчитан.", room.Id, Doc + ", прил. Ж"));
            }
            else
            {
                var root = Math.Sqrt(room.AirDensity / (relief.Values["constant"]!.Value * pa * (Math.Pow((ppr + pa) / pa, relief.Values["exponent"]!.Value) - 1)));
                var fc = (relief.Values["safety"]!.Value * kPress * mp / (relief.Values["dischargeCoef"]!.Value * k1 * tau * rho1) * root) - room.OpenAreaM2;
                reliefArea = Math.Max(0, fc);
                issues.Add(new Issue("gas.relief", reliefArea > 0 ? IssueLevel.Warning : IssueLevel.Info,
                    reliefArea > 0
                        ? string.Create(Ru, $"«{room.Name}»: нужен проём (клапан) сброса избыточного давления площадью не менее {reliefArea:0.###} м² (Ж.1, 9.14.2).")
                        : $"«{room.Name}»: проём сброса избыточного давления не требуется (правая часть Ж.1 ≤ 0).", room.Id, Doc + ", прил. Ж"));
            }
        }

        return new GasMassResult(room.Id, cn, rho1, k2, k3, delta, tau, mp, total, reliefArea, issues);
    }
}
