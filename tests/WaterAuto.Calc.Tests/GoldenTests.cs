using System.Text.Json;
using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

/// <summary>
/// Эталоны tests/Golden (формат — tests/Golden/README.md). Правила сторонних программ из блока compat
/// (кольцо на Q/2, множитель расхода, 20 % «по ГидРаВПТ», P_уу по Б.32 в их прочтении) применяются только здесь:
/// в Calc их нет, чтобы не решать за специалиста вопросы В-02 и В-03.
/// </summary>
public class GoldenTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    // Рабочий диапазон оросителя pMin–pMax — данные производителя (data/catalog/sprinklers.json), не норма.
    // Ссылка узла на каталог — поле «sprinkler» (tests/Golden/README.md); без неё диапазон не подставляется.
    private static readonly Dictionary<string, PressureRange> Catalog = LoadCatalog();

    public static TheoryData<string> ActiveCases() => Cases(status => status != "pending");

    [Theory]
    [MemberData(nameof(ActiveCases))]
    public void Golden_case_matches_source(string name)
    {
        var failures = Run(name);
        failures.ShouldBeEmpty();
    }

    /// <summary>
    /// Отложенные эталоны (status = pending) не блокируют CI, но должны читаться и считаться без ошибок.
    /// Fact, а не Theory: когда отложенных не останется, тест должен проходить, а не падать на пустых данных.
    /// </summary>
    [Fact]
    public void Pending_golden_cases_still_solve()
    {
        foreach (var name in CaseNames(status => status == "pending"))
        {
            var (_, solution, _) = Solve(name);
            solution.Issues.Where(i => i.Level == IssueLevel.Error).ShouldBeEmpty(name);
        }
    }

    private static TheoryData<string> Cases(Func<string, bool> filter)
    {
        var data = new TheoryData<string>();
        foreach (var name in CaseNames(filter))
        {
            data.Add(name);
        }

        return data;
    }

    private static List<string> CaseNames(Func<string, bool> filter)
    {
        var names = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(RepoPaths.Golden).Order(StringComparer.Ordinal))
        {
            var expected = Path.Combine(dir, "expected.json");
            if (!File.Exists(expected))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(expected));
            if (filter(doc.RootElement.GetProperty("status").GetString()!))
            {
                names.Add(Path.GetFileName(dir));
            }
        }

        return names;
    }

    private static List<string> Run(string name)
    {
        var (project, solution, network) = Solve(name);

        // При HasErrors словари величин пусты: без этой проверки эталон падал бы с KeyNotFoundException
        // на первом же обращении к NodePressures/PipeLosses, скрывая настоящую причину.
        if (solution.HasErrors)
        {
            Assert.Fail(
                $"Эталон «{name}»: расчёт с ошибкой, величины для сверки не годятся.{Environment.NewLine}"
                + string.Join(Environment.NewLine, solution.Issues.Select(i => $"{i.Code}: {i.Message}")));
        }

        var derived = Derived(project, solution, network);

        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Golden, name, "expected.json")));
        var failures = new List<string>();
        foreach (var check in expected.RootElement.GetProperty("checks").EnumerateArray())
        {
            var what = check.GetProperty("what").GetString()!;
            var node = check.TryGetProperty("node", out var n) ? n.GetString() : null;
            var pipe = check.TryGetProperty("pipe", out var p) ? p.GetString() : null;
            double actual = what switch
            {
                "q" => solution.EmitterFlows[node!].LitersPerSecond,
                "P" => solution.NodePressures[node!].Mpa,
                "Q" => solution.PipeFlows[pipe!].LitersPerSecond,
                "dP" => solution.PipeLosses[pipe!].Mpa,
                "v" => solution.PipeVelocities[pipe!].MetersPerSecond,
                "Kt" => network.Pipes.Single(x => x.Id == pipe).Characteristic.Kt,
                "Qc" => solution.TotalFlow.LitersPerSecond,
                "Psupply" => derived[$"Psupply:{pipe}"],
                _ => derived[what],
            };
            var value = check.GetProperty("value").GetDouble();
            var tol = check.GetProperty("tol").GetDouble();
            if (!double.IsFinite(actual) || Math.Abs(actual - value) > tol + 1e-12)
            {
                failures.Add($"{what} {node ?? pipe}: ждали {value} ± {tol}, получили {actual:G9}");
            }
        }

        return failures;
    }

    private static (JsonElement Project, TreeSolution Solution, HydraulicNetwork Network) Solve(string name)
    {
        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Golden, name, "input.waproj")));
        var project = doc.RootElement.Clone();
        var compat = project.TryGetProperty("compat", out var c) ? c : default;
        var localFactor = project.GetProperty("settings").GetProperty("localLossFactor").GetDouble();
        var ringHalf = compat.ValueKind == JsonValueKind.Object && compat.TryGetProperty("ringHalfFlow", out var rh) && rh.GetBoolean();
        var divisor = compat.ValueKind == JsonValueKind.Object && compat.TryGetProperty("pipeFlowDivisor", out var d) ? d.GetDouble() : 1.0;
        var exempt = compat.ValueKind == JsonValueKind.Object && compat.TryGetProperty("pipeFlowDivisorExemptKinds", out var ex)
            ? ex.EnumerateArray().Select(e => e.GetString()).ToHashSet(StringComparer.Ordinal)
            : [];

        var net = project.GetProperty("network");
        var nodes = net.GetProperty("nodes").EnumerateArray().Select(n => new NetworkNode(
            n.GetProperty("id").GetString()!,
            n.GetProperty("type").GetString() switch
            {
                "sprinkler" => NodeKind.Sprinkler,
                "hydrant" => NodeKind.Hydrant,
                "source" => NodeKind.Source,
                _ => NodeKind.Tee,
            },
            new Length(n.GetProperty("z").GetDouble()),
            n.TryGetProperty("K", out var k) ? new KFactor(k.GetDouble()) : null,
            CatalogRange(n))).ToList();

        var pipes = net.GetProperty("pipes").EnumerateArray().Select(p =>
        {
            var multiplier = 1 + localFactor;
            if (ringHalf && p.TryGetProperty("ring", out var ring) && ring.GetBoolean())
            {
                multiplier /= 4; // участок считается на Q/2
            }

            var kind = p.TryGetProperty("kind", out var kd) ? kd.GetString() : null;
            if (kind is null || !exempt.Contains(kind))
            {
                multiplier /= divisor * divisor;
            }

            return new NetworkPipe(
                p.GetProperty("id").GetString()!, p.GetProperty("from").GetString()!, p.GetProperty("to").GetString()!,
                new Length(p.GetProperty("length").GetDouble()), Characteristic(p), multiplier, Geometry(p));
        }).ToList();

        var network = new HydraulicNetwork(nodes, pipes);
        var dictating = project.GetProperty("dictating");
        var solution = TreeSolver.SolveDesign(network, dictating.GetProperty("node").GetString()!, new Pressure(dictating.GetProperty("P").GetDouble()));
        return (project, solution, network);
    }

    private static PipeCharacteristic Characteristic(JsonElement pipe)
    {
        if (pipe.TryGetProperty("A", out var a))
        {
            return PipeCharacteristic.FromResistivity(a.GetDouble());
        }

        var resolved = PipeCharacteristics.Resolve(Norms, Geometry(pipe)!)!;
        if (!resolved.DnMatches)
        {
            // Эталон сам ошибочен: Kт найден по D и стенке, но заявленный DN не тот, что в строке табл. Б.2.
            throw new InvalidOperationException(
                $"Эталон, труба «{pipe.GetProperty("id").GetString()}»: заявлен DN {resolved.RequestedDn}, "
                + $"а строка {resolved.NormId} табл. Б.2 — DN {resolved.TableDn}. Сверка идёт по D и стенке, DN — проверяемое поле.");
        }

        return resolved.Value;
    }

    // Сортамент есть у стальных труб (standard, od, wall); книжные примеры через A его не задают.
    private static PipeGeometry? Geometry(JsonElement pipe) =>
        pipe.TryGetProperty("standard", out var standard)
            ? new PipeGeometry(
                standard.GetString()!,
                pipe.GetProperty("dn").GetInt32(),
                Length.FromMillimeters(pipe.GetProperty("od").GetDouble()),
                Length.FromMillimeters(pipe.GetProperty("wall").GetDouble()))
            : null;

    private static Dictionary<string, PressureRange> LoadCatalog()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "data", "catalog", "sprinklers.json")));
        return doc.RootElement.GetProperty("sprinklers").EnumerateArray().ToDictionary(
            s => s.GetProperty("id").GetString()!,
            s => new PressureRange(new Pressure(s.GetProperty("pMin").GetDouble()), new Pressure(s.GetProperty("pMax").GetDouble())),
            StringComparer.Ordinal);
    }

    // Узел без поля «sprinkler» (или с неизвестным id) диапазона не получает — проверка не выполняется.
    private static PressureRange? CatalogRange(JsonElement node) =>
        node.TryGetProperty("sprinkler", out var id) && Catalog.TryGetValue(id.GetString()!, out var range) ? range : null;

    // Величины после сети (узел управления, подводящие, насос) — по правилам compat источника.
    private static Dictionary<string, double> Derived(JsonElement project, TreeSolution solution, HydraulicNetwork network)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (!project.TryGetProperty("controlValve", out var cv))
        {
            return result;
        }

        var compat = project.GetProperty("compat");
        var divisor = compat.TryGetProperty("pipeFlowDivisor", out var d) ? d.GetDouble() : 1.0;
        var q = solution.TotalFlow.LitersPerSecond;
        var qValve = cv.GetProperty("flowUnit").GetString() == "m3/h" ? q * 3.6 : q;
        result["Puu"] = cv.GetProperty("xi").GetDouble() * cv.GetProperty("gamma").GetDouble() * qValve * qValve;

        var pathLosses = solution.DictatingPath.Sum(id => solution.PipeLosses[id].Mpa);
        var source = network.Nodes.Single(n => n.Kind == NodeKind.Source);
        var pressure = solution.SourcePressure.Mpa + result["Puu"] + (compat.GetProperty("pathLocalLossFactor").GetDouble() * pathLosses);
        result["Psection"] = pressure;

        var pump = project.GetProperty("pump");
        var supplyLosses = 0.0;
        foreach (var s in pump.GetProperty("supplyPipes").EnumerateArray())
        {
            var kt = Characteristic(s).Kt;
            var loss = q * q * s.GetProperty("length").GetDouble() / (100 * kt) / (divisor * divisor);
            supplyLosses += loss;
            pressure += loss;
            result[$"Psupply:{s.GetProperty("id").GetString()}"] = pressure;
        }

        pressure += compat.GetProperty("supplyLocalLossFactor").GetDouble() * supplyLosses;
        result["PwithLocal"] = pressure;
        result["Ppump"] = pressure + ((source.Elevation.Meters - pump.GetProperty("axisZ").GetDouble()) / 100) - pump.GetProperty("inletPressure").GetDouble();
        return result;
    }
}
