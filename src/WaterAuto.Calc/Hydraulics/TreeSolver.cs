using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

/// <summary>Результат расчёта тупиковой сети в проектном режиме (README §5.4).</summary>
public sealed record TreeSolution(
    Pressure SourcePressure,
    Flow TotalFlow,
    IReadOnlyDictionary<string, Pressure> NodePressures,
    IReadOnlyDictionary<string, Flow> PipeFlows,
    IReadOnlyDictionary<string, Pressure> PipeLosses,
    IReadOnlyDictionary<string, Flow> EmitterFlows,
    IReadOnlyList<string> DictatingPath,
    IReadOnlyList<Issue> Issues)
{
    public bool HasErrors => Issues.Any(i => i.Level == IssueLevel.Error);
}

/// <summary>
/// Последовательный расчёт тупиковой сети по приложению Б (Б.9–Б.23): от диктующего оросителя к источнику.
/// Боковые ветви сворачиваются в характеристику C (Q = C·√(h − z/100), B = C² — характеристика рядка Б.17);
/// при подключении расход ветви пересчитывается по фактическому давлению узла (Б.20–Б.21).
/// Для дерева, в каждой боковой ветви которого все эмиттеры на одной отметке, это точное решение.
/// Кольцевые сети — этап 4 (ADR 0002).
/// </summary>
public static class TreeSolver
{
    public static TreeSolution SolveDesign(HydraulicNetwork network, string dictatingNodeId, Pressure dictatingPressure)
    {
        ArgumentNullException.ThrowIfNull(network);
        var issues = new List<Issue>();
        var nodes = new Dictionary<string, NetworkNode>(StringComparer.Ordinal);
        foreach (var n in network.Nodes)
        {
            if (!nodes.TryAdd(n.Id, n))
            {
                issues.Add(new Issue("topology.duplicateNode", IssueLevel.Error, $"Узел {n.Id} задан дважды.", n.Id));
            }
        }

        var incoming = new Dictionary<string, NetworkPipe>(StringComparer.Ordinal);
        var children = new Dictionary<string, List<NetworkPipe>>(StringComparer.Ordinal);
        foreach (var p in network.Pipes)
        {
            if (!nodes.ContainsKey(p.From) || !nodes.ContainsKey(p.To))
            {
                issues.Add(new Issue("topology.danglingPipe", IssueLevel.Error, $"Труба {p.Id} ссылается на несуществующий узел.", p.Id));
                continue;
            }

            if (p.Length.Meters <= 0)
            {
                issues.Add(new Issue("topology.zeroLength", IssueLevel.Error, $"Труба {p.Id} имеет нулевую длину.", p.Id));
            }

            if (p.Characteristic.Kt <= 0 || p.LossMultiplier <= 0)
            {
                issues.Add(new Issue("pipe.characteristic", IssueLevel.Error, $"Труба {p.Id}: Kт и множитель потерь должны быть больше нуля.", p.Id));
            }

            if (!incoming.TryAdd(p.To, p))
            {
                issues.Add(new Issue("topology.notTree", IssueLevel.Error, $"К узлу {p.To} подходит больше одной трубы: кольцевые сети — этап 4.", p.To));
            }

            if (!children.TryGetValue(p.From, out var list))
            {
                children[p.From] = list = [];
            }

            list.Add(p);
        }

        var sources = network.Nodes.Where(n => n.Kind == NodeKind.Source).ToList();
        if (sources.Count != 1)
        {
            issues.Add(new Issue("topology.source", IssueLevel.Error, $"Нужен ровно один источник, задано {sources.Count}."));
        }

        if (!nodes.TryGetValue(dictatingNodeId, out var dictating) || dictating.Emitter is null)
        {
            issues.Add(new Issue("dictating.missing", IssueLevel.Error, $"Диктующий узел {dictatingNodeId} не найден или не является оросителем.", dictatingNodeId));
        }

        if (issues.Any(i => i.Level == IssueLevel.Error))
        {
            return Failed(issues);
        }

        var source = sources[0];
        if (incoming.ContainsKey(source.Id))
        {
            return Failed([.. issues, new Issue("topology.sourceFed", IssueLevel.Error, "В источник не может входить труба.", source.Id)]);
        }

        var unreachable = nodes.Keys.Where(id => id != source.Id && !Reaches(id, source.Id, incoming)).ToList();
        if (unreachable.Count > 0)
        {
            return Failed([.. issues, .. unreachable.Select(id => new Issue("topology.disconnected", IssueLevel.Error, $"Узел {id} не связан с источником.", id))]);
        }

        // Путь от диктующего оросителя к источнику.
        var path = new List<string> { dictating!.Id };
        var pathPipes = new List<NetworkPipe>();
        while (path[^1] != source.Id)
        {
            var p = incoming[path[^1]];
            pathPipes.Add(p);
            path.Add(p.From);
        }

        var onPath = new HashSet<string>(pathPipes.Select(p => p.Id), StringComparer.Ordinal);
        var characteristic = new Dictionary<string, (double C, double EmitterHead)>(StringComparer.Ordinal);
        foreach (var p in network.Pipes.Where(p => !onPath.Contains(p.Id)))
        {
            Fold(p, children, nodes, characteristic, issues);
        }

        if (issues.Any(i => i.Level == IssueLevel.Error))
        {
            return Failed(issues);
        }

        var head = new Dictionary<string, double>(StringComparer.Ordinal);
        var pipeFlows = new Dictionary<string, Flow>(StringComparer.Ordinal);
        var losses = new Dictionary<string, Pressure>(StringComparer.Ordinal);
        var emitters = new Dictionary<string, Flow>(StringComparer.Ordinal);

        // h = P + z/100 — пьезометрическое давление (README §5.3).
        head[dictating.Id] = dictatingPressure.Mpa + (dictating.Elevation.Meters / 100.0);
        var flow = NodeOutflow(dictating, null, head[dictating.Id], children, nodes, characteristic, head, pipeFlows, losses, emitters, issues);
        for (var i = 0; i < pathPipes.Count; i++)
        {
            var p = pathPipes[i];
            var r = Resistance(p);
            pipeFlows[p.Id] = new Flow(flow);
            losses[p.Id] = new Pressure(r * flow * flow);
            var upstream = nodes[p.From];
            head[upstream.Id] = head[p.To] + (r * flow * flow);
            flow += NodeOutflow(upstream, p.Id, head[upstream.Id], children, nodes, characteristic, head, pipeFlows, losses, emitters, issues);
        }

        var pressures = head.ToDictionary(kv => kv.Key, kv => new Pressure(kv.Value - (nodes[kv.Key].Elevation.Meters / 100.0)), StringComparer.Ordinal);
        return new TreeSolution(pressures[source.Id], new Flow(flow), pressures, pipeFlows, losses, emitters, [.. pathPipes.Select(p => p.Id)], issues);
    }

    /// <summary>r в МПа/(л/с)²: ΔP = r·Q², r = L/(100·Kт)·множитель (Б.6, Б.31).</summary>
    public static double Resistance(NetworkPipe pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        return pipe.Length.Meters / (100.0 * pipe.Characteristic.Kt) * pipe.LossMultiplier;
    }

    /// <summary>Расход оросителя q = 10·K·√P (Б.4); при P ≤ 0 — 0.</summary>
    public static Flow EmitterFlow(KFactor k, Pressure p) => new(p.Mpa > 0 ? 10.0 * k.Value * Math.Sqrt(p.Mpa) : 0);

    // Расход, отбираемый в узле: свой эмиттер и боковые ветви (кроме трубы пути, по которой пришли).
    private static double NodeOutflow(
        NetworkNode node, string? pathPipeId, double h,
        Dictionary<string, List<NetworkPipe>> children, Dictionary<string, NetworkNode> nodes,
        Dictionary<string, (double C, double EmitterHead)> characteristic,
        Dictionary<string, double> head, Dictionary<string, Flow> pipeFlows, Dictionary<string, Pressure> losses,
        Dictionary<string, Flow> emitters, List<Issue> issues)
    {
        var total = 0.0;
        if (node.Emitter is { } k)
        {
            var q = EmitterFlow(k, new Pressure(h - (node.Elevation.Meters / 100.0)));
            emitters[node.Id] = q;
            total += q.LitersPerSecond;
        }

        foreach (var p in children.GetValueOrDefault(node.Id, []))
        {
            if (p.Id == pathPipeId || characteristic.ContainsKey(p.Id) is false)
            {
                continue;
            }

            total += Distribute(p, h, children, nodes, characteristic, head, pipeFlows, losses, emitters, issues);
        }

        return total;
    }

    // Подставляет давление узла подключения в свёрнутую ветвь и раскладывает расходы по её трубам (Б.20–Б.21).
    private static double Distribute(
        NetworkPipe pipe, double hFrom,
        Dictionary<string, List<NetworkPipe>> children, Dictionary<string, NetworkNode> nodes,
        Dictionary<string, (double C, double EmitterHead)> characteristic,
        Dictionary<string, double> head, Dictionary<string, Flow> pipeFlows, Dictionary<string, Pressure> losses,
        Dictionary<string, Flow> emitters, List<Issue> issues)
    {
        var (c, emitterHead) = characteristic[pipe.Id];
        var available = hFrom - emitterHead;
        if (c == 0 || available <= 0)
        {
            if (c > 0)
            {
                issues.Add(new Issue("branch.noPressure", IssueLevel.Warning, $"Давления в узле {pipe.From} не хватает для ветви через {pipe.Id}.", pipe.Id));
            }

            available = Math.Max(available, 0);
        }

        var q = c * Math.Sqrt(available);
        var r = Resistance(pipe);
        pipeFlows[pipe.Id] = new Flow(q);
        losses[pipe.Id] = new Pressure(r * q * q);
        var to = nodes[pipe.To];
        head[to.Id] = hFrom - (r * q * q);
        NodeOutflow(to, null, head[to.Id], children, nodes, characteristic, head, pipeFlows, losses, emitters, issues);
        return q;
    }

    // Характеристика ветви, начинающейся трубой pipe: C и отметка эмиттеров (как пьезометрическое давление z/100).
    private static (double C, double EmitterHead) Fold(
        NetworkPipe pipe, Dictionary<string, List<NetworkPipe>> children, Dictionary<string, NetworkNode> nodes,
        Dictionary<string, (double C, double EmitterHead)> memo, List<Issue> issues)
    {
        if (memo.TryGetValue(pipe.Id, out var done))
        {
            return done;
        }

        var node = nodes[pipe.To];
        var c = 0.0;
        double? level = null;
        if (node.Emitter is { } k)
        {
            c += 10.0 * k.Value;
            level = node.Elevation.Meters / 100.0;
        }

        foreach (var child in children.GetValueOrDefault(node.Id, []))
        {
            var (cc, lv) = Fold(child, children, nodes, memo, issues);
            if (cc == 0)
            {
                continue;
            }

            if (level is { } l && Math.Abs(l - lv) > 1e-12)
            {
                issues.Add(new Issue("branch.mixedElevation", IssueLevel.Error,
                    $"В ветви от {pipe.Id} эмиттеры на разных отметках: последовательный расчёт неточен, нужен солвер (этап 4).", pipe.Id));
            }

            level ??= lv;
            c += cc;
        }

        // Последовательно с трубой: h − z_e = Q²/C² + r·Q²  ⇒  C' = 1/√(1/C² + r).
        var result = c > 0 ? (1.0 / Math.Sqrt((1.0 / (c * c)) + Resistance(pipe)), level!.Value) : (0.0, 0.0);
        memo[pipe.Id] = result;
        return result;
    }

    private static bool Reaches(string id, string sourceId, Dictionary<string, NetworkPipe> incoming)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = id;
        while (current != sourceId)
        {
            if (!seen.Add(current) || !incoming.TryGetValue(current, out var p))
            {
                return false;
            }

            current = p.From;
        }

        return true;
    }

    private static TreeSolution Failed(List<Issue> issues) =>
        new(Pressure.Zero, Flow.Zero, new Dictionary<string, Pressure>(), new Dictionary<string, Flow>(),
            new Dictionary<string, Pressure>(), new Dictionary<string, Flow>(), [], issues);
}
