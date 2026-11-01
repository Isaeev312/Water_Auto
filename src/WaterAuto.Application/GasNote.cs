using System.Globalization;
using WaterAuto.Core;
using WaterAuto.Reports;

namespace WaterAuto.Application;

/// <summary>
/// Пояснительная записка АУГП (объёмное тушение): исходные данные помещений, нормативная концентрация и плотность ГОТВ
/// (прил. Г), расчёт массы по прил. Д с подстановкой, проём сброса давления по прил. Ж, требования разд. 9 к установке.
/// </summary>
public static class GasNote
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static NoteDocument Build(NoteTitle title, IReadOnlyList<(GasRoom Room, string Agent, GasMassResult Result)> rooms, double altitude, IReadOnlyList<Issue> issues, INormsProvider norms,
        IReadOnlyList<(string Room, string Module, GasModulesResult Result)>? modules = null)
    {
        ArgumentNullException.ThrowIfNull(norms);
        double V(string id, string key) => norms.Find(id)!.Values[key]!.Value;
        var minFill = V("sp485.p9_6_5.minFill", "minFillPercent");
        var reserve = V("sp485.p9_6.reserve", "reservePercent");
        var discharge = norms.Find("sp485.p9_7_4.dischargeTime")!.Values;
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(issues);
        static string F(double v, string fmt = "0.##") => v.ToString(fmt, Ru);
        var sections = new List<NoteSection>
        {
            new("1", "Исходные данные",
            [
                new NoteParagraph(string.Create(Ru, $"Автоматическая установка газового пожаротушения (АУГП) объёмным способом проектируется по СП 485.1311500.2020, разд. 9 и прил. Г, Д, Ж. Защищаемые помещения, ГОТВ, горючий материал, проёмы и температура — исходные данные проектировщика (п. 9.5). Высота объекта над уровнем моря — {F(altitude, "0")} м (поправочный коэффициент K3 по табл. Г.17).")),
                new NoteTable("Защищаемые помещения", ["Помещение", "Vр, м³", "H, м", "ГОТВ", "Материал", "ΣFн, м²", "Tм, °С"],
                    [.. rooms.Select(x => (IReadOnlyList<string>)[x.Room.Name, F(x.Room.VolumeM3, "0"), F(x.Room.HeightM), x.Agent, x.Room.Material, F(x.Room.OpenAreaM2, "0.###"), F(x.Room.MinTemperatureC, "0")])]),
            ]),
            new("2", "Расчёт массы ГОТВ",
            [
                new NoteFormula("*M*_{г} = *K*_{1}·[*M*_{р} + *M*_{тр} + *M*_{б}·*n*],", "2.1", ["*K*_{1} = 1,05 — утечки из сосудов (Д.2.1); *M*_{тр} — остаток в трубопроводах (Д.5, по гидравлическому расчёту); *M*_{б}·*n* — остаток в модулях по ТД (формула Д.1)."]),
                new NoteFormula("*M*_{р} = *V*_{р}·ρ_{1}·(1 + *K*_{2})·*C*_{н}/(100 − *C*_{н}) — сжиженные ГОТВ;   *M*_{р} = *V*_{р}·ρ_{1}·(1 + *K*_{2})·ln[100/(100 − *C*_{н})] — сжатые и CO₂,", "2.2",
                [
                    "ρ_{1} = ρ_{0}·*T*_{0}/*T*_{м}·*K*_{3} — плотность ГОТВ при минимальной температуре (Д.4), ρ_{0} и *C*_{н} — прил. Г;",
                    "*K*_{2} = П·δ·τ_{под}·√*H* — потери через проёмы (Д.7), δ = Σ*F*_{н}/*V*_{р} — параметр негерметичности (Д.8), не более табл. Г.16.",
                ]),
                new NoteTable("Результат", ["Помещение", "Cн, %", "ρ1, кг/м³", "K3", "δ, м⁻¹", "τпод, с", "K2", "Mр, кг", "Mг, кг"],
                    [.. rooms.Select(x => (IReadOnlyList<string>)[x.Room.Name, F(x.Result.Concentration, "0.0"), F(x.Result.Rho1, "0.###"), F(x.Result.K3, "0.###"), F(x.Result.Delta, "0.######"), F(x.Result.DischargeTimeS, "0"), F(x.Result.K2, "0.#####"), F(x.Result.DesignMass, "0.#"), F(x.Result.TotalMass, "0")])]),
                .. rooms.Select(x => (NoteBlock)new NoteParagraph(string.Create(Ru,
                    $"{x.Room.Name}: Mр = {F(x.Room.VolumeM3, "0")}·{F(x.Result.Rho1, "0.###")}·(1 + {F(x.Result.K2, "0.#####")})·{(x.Result.Concentration > 0 ? $"{F(x.Result.Concentration, "0.0")}/(100 − {F(x.Result.Concentration, "0.0")})" : "—")} = {F(x.Result.DesignMass, "0.#")} кг; Mг = 1,05·({F(x.Result.DesignMass, "0.#")} + {F(x.Room.PipeResidueKg ?? 0, "0.#")} + {F(x.Room.ModuleResidueKg * x.Room.Modules, "0.#")}) = {F(x.Result.TotalMass, "0")} кг."))),
                .. modules is { Count: > 0 }
                    ? new NoteBlock[] { new NoteTable(string.Create(Ru, $"Модули (Mг по Д.1 с остатком Mб·n; наполнение не менее {minFill:0} % максимального, п. 9.6.5)"), ["Помещение", "Модуль", "n, шт.", "ГОТВ в модуле, кг", "Наполнение, %"],
                        [.. modules.Select(m => (IReadOnlyList<string>)[m.Room, m.Module, m.Result.Modules.ToString(Ru), F(m.Result.MassPerModule, "0.#"), F(m.Result.FillPercent, "0")])]) }
                    : [],
                new NoteParagraph(string.Create(Ru, $"Масса ГОТВ на объект — по наибольшему помещению (направлению): {F(rooms.Max(x => x.Result.TotalMass), "0")} кг; кроме расчётного количества — {F(reserve, "0")} % запас (модульные) или резерв (централизованные), п. 9.6.2, 9.6.3. Количество и типоразмер модулей — по ТД изготовителя (наполнение не менее {F(minFill, "0")} % максимального, п. 9.6.5).")),
            ]),
            new("3", "Проём для сброса избыточного давления",
            [
                new NoteFormula("*F*_{с} ≥ *K*_{зап}·*K*_{дав}·*M*_{р}/(0,7·*K*_{1}·τ_{под}·ρ_{1})·√(ρ_{в}/(7·10⁶·*P*_{а}·[((*P*_{пр} + *P*_{а})/*P*_{а})^{0,2857} − 1])) − Σ*F*,", "3.1",
                    ["*K*_{зап} = 1,2; *K*_{дав} = 1 для сжиженных ГОТВ, 2,4 — азот, 2,66 — аргон, 2,44 — «Инерген» (прил. Ж); *P*_{пр} — предельно допустимое избыточное давление (ГОСТ Р 12.3.047)."]),
                new NoteTable("Площадь проёма", ["Помещение", "Pпр, МПа", "Fс, м²"],
                    [.. rooms.Select(x => (IReadOnlyList<string>)[x.Room.Name, x.Room.MaxOverpressureMPa is { } p ? F(p, "0.####") : "не задано", x.Result.ReliefAreaM2 is { } a ? (a > 0 ? F(a, "0.###") : "не требуется") : "не рассчитан"])]),
            ]),
            new("4", "Требования к установке",
            [
                new NoteList(
                [
                    "автоматический (основной) и дистанционный пуск; задержка выпуска ГОТВ на эвакуацию, отключение вентиляции и закрытие клапанов — не менее 10 с от включения СОУЭ (пп. 9.2.2, 9.7.1);",
                    $"все сосуды одного помещения включаются в интервале не более {F(V("sp485.p9_7_2.startInterval", "maxInterval"))} с (п. 9.7.2); подача {F(discharge["minMassPercent"]!.Value)} % массы — за {F(discharge["modularLiquefied"]!.Value)} с (модульные со сжиженными ГОТВ), {F(discharge["centralLiquefied"]!.Value)} с (централизованные), {F(discharge["co2OrCompressed"]!.Value)} с (CO₂, сжатые газы) (п. 9.7.4);",
                    $"модули — при температуре {F(V("sp485.p9_8_4.moduleTemperature", "min")).Replace("-", "−", StringComparison.Ordinal)}…{F(V("sp485.p9_8_4.moduleTemperature", "max"))} °С, не ближе {F(V("sp485.p9_8_1.heatSource", "minDistance"))} м от источников тепла (пп. 9.8.1, 9.8.4); насадки ГОТВ тяжелее воздуха — не дальше {F(V("sp485.p9_11_3.nozzleCeiling", "maxFromCeiling"))} м от перекрытия (п. 9.11.3);",
                    "доводчики дверей, уплотнение кабельных проходок, указатели и сигнализация у входов «Газ — уходи!», «Газ — не входи!» (пп. 9.14.1, 9.14.4); удаление ГОТВ после тушения — по СП 7.13130 (п. 9.14.5);",
                    $"трубопроводы — стальные, прочность {F(V("sp485.p9_9_5.pipeStrength", "strengthFactor"))}·Pраб, заземление, зазор от стены не менее {F(V("sp485.p9_9_4.pipeWallGap", "minGap"))} см (пп. 9.9.1–9.9.7); устройства ручного пуска — на высоте не более {F(V("sp485.p9_10_4.remoteStartHeight", "maxHeight"))} м, опломбированы (пп. 9.10.4, 9.16.2).",
                ]),
                new NotePending("Гидравлический расчёт (диаметры, насадки, время подачи, остаток в трубопроводах) — по методике изготовителя установки (п. 9.4.2); до него масса предварительная (вопрос А-18)."),
            ]),
        };
        var problems = issues.Where(i => i.Code.StartsWith("gas.", StringComparison.Ordinal) && i.Level != IssueLevel.Info).ToList();
        if (problems.Count > 0)
        {
            sections.Add(new("5", "Замечания расчёта", [.. problems.Select(i => (NoteBlock)new NotePending($"{i.Level.Ru()}: {i.DocumentText()}"))]));
        }

        return new NoteDocument(title with { Title = "Установка газового пожаротушения. Пояснительная записка" }, sections, []);
    }
}
