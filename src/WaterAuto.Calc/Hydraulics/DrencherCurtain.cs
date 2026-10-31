using System.Globalization;
using WaterAuto.Calc.Routing;
using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

/// <summary>
/// Проём под водяную завесу — решение проектировщика: ширина, м; K дренчерного оросителя; давление у диктующего (последнего)
/// оросителя нитки, МПа (по ТД оросителя); DN распределительного трубопровода; группа помещений (продолжительность 6.3.6);
/// завеса совмещена с АУП-С (продолжительность — как у АУП-С, расход — по Б.28).
/// </summary>
public sealed record CurtainInput(string Id, string Name, double WidthM, double K, double DictatingPressureMpa, int Dn, string Group, bool CombinedWithSprinkler = false);

/// <summary>Результат: число ниток и оросителей в нитке, шаг, расход завесы, давление в точке питания нитки, продолжительность, зона без нагрузки.</summary>
public sealed record CurtainResult(CurtainInput Input, int Rows, int PerRow, double SpacingM, double RowSpecificFlow, Flow TotalFlow, Pressure FeedPressure, double Duration, double FreeZoneM, IReadOnlyList<Issue> Issues);

/// <summary>
/// Дренчерная водяная завеса (СП 485, пп. 6.3.10, 6.3.11, 6.3.6, Б.1.3.4–Б.1.3.6): проём уже порога — одна нитка с удельным
/// расходом 1 л/(с·м), шире — две нитки по 0,5 л/(с·м) с крайними оросителями не дальше 0,5 м от стены. Шаг — из расхода
/// диктующего оросителя q = 10·K·√p; оросители равномерно, крайние — в половине шага от стен. Нитка считается от питания с конца
/// тем же решателем, что и сеть АУП (Б.27: расход — сумма расходов оросителей); местные потери — по Б.1.3.13.
/// </summary>
public static class DrencherCurtain
{
    private const string Doc = "СП 485.1311500.2020";
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static CurtainResult Calculate(CurtainInput input, PipeSeries series, INormsProvider norms)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(norms);
        var issues = new List<Issue>();
        CurtainResult Fail(string code, string message) =>
            new(input, 0, 0, 0, 0, new Flow(0), Pressure.Zero, 0, 0, [.. issues, new Issue(code, IssueLevel.Error, message, input.Id, Doc + ", п. 6.3")]);
        var one = norms.Find("sp485.p6_3_10.singleRow");
        var two = norms.Find("sp485.p6_3_11.twoRows");
        var duration = norms.Find("sp485.p6_3_6.duration");
        var zone = norms.Find("sp485.p6_3_13.freeZone");
        var local = norms.Find("sp485.b1_3_13.localLossFactor");
        if (one is null || two is null || duration is null || zone is null || local is null)
        {
            return Fail("curtain.noNorm", "В данных норм нет пп. 6.3.6–6.3.13 (data/norms/sp485/clauses_6_3_curtain.json).");
        }

        if (input.WidthM <= 0 || input.K <= 0 || input.DictatingPressureMpa <= 0)
        {
            return Fail("curtain.input", $"Завеса «{input.Name}»: ширина, K и давление у оросителя должны быть больше нуля.");
        }

        if (series.Pick(input.Dn) is not { } pipe)
        {
            return Fail("curtain.noPipe", $"Завеса «{input.Name}»: DN{input.Dn} нет в сортаменте.");
        }

        var twoRows = input.WidthM >= two.Values["fromWidth"]!.Value - 1e-9;
        var rows = twoRows ? 2 : 1;
        var specific = twoRows ? two.Values["rowFlow"]!.Value : one.Values["specificFlow"]!.Value;
        var q1 = 10 * input.K * Math.Sqrt(input.DictatingPressureMpa);
        var n = (int)Math.Ceiling((input.WidthM * specific / q1) - 1e-9);
        if (twoRows)
        {
            // Крайние оросители — в половине шага от стены, не дальше wallMax: шаг ≤ 2·wallMax.
            n = Math.Max(n, (int)Math.Ceiling((input.WidthM / (2 * two.Values["wallMax"]!.Value)) - 1e-9));
        }

        n = Math.Max(n, 1);
        var spacing = input.WidthM / n;

        // Нитка: питание с конца, первый ороситель — в половине шага от стены, далее через шаг; диктующий — последний.
        var k = local.Values["localLossFactor"]!.Value;
        var nodes = new List<NetworkNode> { new("S", NodeKind.Source, new Length(0)) };
        var pipes = new List<NetworkPipe>();
        for (var j = 1; j <= n; j++)
        {
            nodes.Add(new($"D{j}", NodeKind.Sprinkler, new Length(0), new KFactor(input.K)));
            pipes.Add(new($"{input.Id}.P{j}", j == 1 ? "S" : $"D{j - 1}", $"D{j}", new Length(j == 1 ? spacing / 2 : spacing), pipe.Kt, 1 + k,
                new PipeGeometry(pipe.Standard, pipe.Dn, new Length(pipe.OuterDiameterMm / 1000), new Length(pipe.WallMm / 1000))));
        }

        var solution = TreeSolver.SolveDesign(new HydraulicNetwork(nodes, pipes), $"D{n}", new Pressure(input.DictatingPressureMpa));
        issues.AddRange(solution.Issues);
        var rowFlow = solution.TotalFlow.LitersPerSecond;
        if (rowFlow / input.WidthM < specific - 1e-9)
        {
            issues.Add(new Issue("curtain.specificFlow", IssueLevel.Error,
                string.Create(Ru, $"Завеса «{input.Name}»: удельный расход нитки {rowFlow / input.WidthM:0.00} л/(с·м) меньше {specific:0.0}."), input.Id, Doc + (twoRows ? ", п. 6.3.11" : ", п. 6.3.10")));
        }

        var t = input.CombinedWithSprinkler ? double.NaN : input.Group == "1" ? duration.Values["group1"]!.Value : duration.Values["other"]!.Value;
        issues.Add(new Issue("curtain.start", IssueLevel.Info,
            $"Завеса «{input.Name}»: пуск автоматически и вручную (п. 6.3.8), устройства пуска — у проёма снаружи или на ближайшем пути эвакуации (п. 6.3.14); при делении помещений — зона без пожарной нагрузки не менее {zone.Values["freeZone"]:0} м в обе стороны (п. 6.3.13).",
            input.Id, Doc + ", пп. 6.3.8, 6.3.13, 6.3.14"));
        return new(input, rows, n, spacing, specific, new Flow(rowFlow * rows), solution.SourcePressure, t, zone.Values["freeZone"]!.Value, issues);
    }
}
