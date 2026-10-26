using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

/// <summary>
/// Расчёт сети с кольцами (этап 4, ADR 0011) узловым методом: неизвестные — пьезометрические давления узлов h = P + z/100,
/// уравнения — баланс расходов в узлах; трубы ΔP = r·Q·|Q| (Б.6), эмиттеры q = 10·K·√P (Б.4). Ньютон, линейная система
/// с симметричной положительно определённой матрицей — сопряжёнными градиентами.
/// Проектный режим: давление источника подбирается так, чтобы у диктующего оросителя было заданное давление.
/// На тупиковой сети результат совпадает с <see cref="TreeSolver"/>.
/// </summary>
public static class LoopSolver
{
    // Ниже этого перепада, МПа, зависимость Q(ΔP) трубы линеаризуется (√ не определена в нуле).
    private const double SmallDrop = 1e-12;
    private const double FlowTolerance = 1e-11;

    public static TreeSolution SolveDesign(HydraulicNetwork network, string dictatingNodeId, Pressure dictatingPressure)
    {
        ArgumentNullException.ThrowIfNull(network);
        var issues = new List<Issue>();
        var nodes = network.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var sources = network.Nodes.Where(n => n.Kind == NodeKind.Source).ToList();
        if (sources.Count != 1)
        {
            return Failed(new Issue("topology.source", IssueLevel.Error, $"Нужен ровно один источник, задано {sources.Count}."));
        }

        if (!nodes.TryGetValue(dictatingNodeId, out var dictating) || dictating.Emitter is null)
        {
            return Failed(new Issue("dictating.missing", IssueLevel.Error, $"Диктующий узел {dictatingNodeId} не найден или не является оросителем.", dictatingNodeId));
        }

        var source = sources[0];
        if (network.Pipes.Any(p => !nodes.ContainsKey(p.From) || !nodes.ContainsKey(p.To) || p.Length.Meters <= 0 || p.Characteristic.Kt <= 0))
        {
            return Failed(new Issue("topology.danglingPipe", IssueLevel.Error, "Труба ссылается на несуществующий узел или имеет нулевую длину или Kт."));
        }

        // Давления в сети без перепада отметок пропорциональны давлению источника: решение при H = 1 и масштаб;
        // при разных отметках — уточнение секущими.
        var target = dictatingPressure.Mpa;
        double h0 = 1.0, p0 = Solve(network, source, h0).Pressure(dictating);
        if (p0 <= 0)
        {
            return Failed(new Issue("network.noFlow", IssueLevel.Error, "До диктующего оросителя не доходит вода: проверьте связность сети.", dictatingNodeId));
        }

        var h1 = h0 * target / p0;
        var state = Solve(network, source, h1);
        var p1 = state.Pressure(dictating);
        for (var i = 0; i < 30 && Math.Abs(p1 - target) > 1e-12; i++)
        {
            var next = h1 + ((target - p1) * (h1 - h0) / (p1 - p0));
            (h0, p0) = (h1, p1);
            h1 = next;
            state = Solve(network, source, h1);
            p1 = state.Pressure(dictating);
        }

        if (!state.Converged)
        {
            issues.Add(new Issue("network.notConverged", IssueLevel.Error, "Расчёт кольцевой сети не сошёлся.", dictatingNodeId));
        }

        var pressures = network.Nodes.ToDictionary(n => n.Id, n => new Pressure(state.Pressure(n)), StringComparer.Ordinal);
        var flows = network.Pipes.ToDictionary(p => p.Id, p => new Flow(state.Flow(p)), StringComparer.Ordinal);
        var losses = network.Pipes.ToDictionary(p => p.Id, p => new Pressure(state.Head[p.From] - state.Head[p.To]), StringComparer.Ordinal);
        var emitters = network.Nodes.Where(n => n.Emitter is not null).ToDictionary(n => n.Id, n => TreeSolver.EmitterFlow(n.Emitter!.Value, pressures[n.Id]), StringComparer.Ordinal);
        var total = network.Pipes.Where(p => p.From == source.Id).Sum(p => flows[p.Id].LitersPerSecond) - network.Pipes.Where(p => p.To == source.Id).Sum(p => flows[p.Id].LitersPerSecond);
        return new TreeSolution(pressures[source.Id], new Flow(total), pressures, flows, losses, emitters, new Dictionary<string, Velocity>(), [], issues);
    }

    private static TreeSolution Failed(Issue issue) =>
        new(Pressure.Zero, new Flow(0), new Dictionary<string, Pressure>(), new Dictionary<string, Flow>(), new Dictionary<string, Pressure>(), new Dictionary<string, Flow>(), new Dictionary<string, Velocity>(), [], [issue]);

    private sealed class State(Dictionary<string, double> head, Dictionary<string, NetworkNode> nodes, bool converged)
    {
        public Dictionary<string, double> Head { get; } = head;

        public bool Converged { get; } = converged;

        public double Pressure(NetworkNode n) => Head[n.Id] - (nodes[n.Id].Elevation.Meters / 100.0);

        public double Flow(NetworkPipe p) => PipeFlow(p, Head[p.From] - Head[p.To]).Q;
    }

    // Q = ΔP / √(r·|ΔP|) и производная dQ/dΔP; при малом перепаде — линейно.
    private static (double Q, double G) PipeFlow(NetworkPipe p, double drop)
    {
        var r = TreeSolver.Resistance(p);
        var a = Math.Max(Math.Abs(drop), SmallDrop);
        return (drop / Math.Sqrt(r * a), Math.Abs(drop) > SmallDrop ? 0.5 / Math.Sqrt(r * a) : 1 / Math.Sqrt(r * a));
    }

    // Эмиттер: q = 10·K·√P и dq/dP; при малом давлении — линейно.
    private static (double Q, double G) EmitterFlow(double k, double p)
    {
        if (p <= 0)
        {
            return (0, 10 * k / Math.Sqrt(SmallDrop * 1e3));
        }

        var a = Math.Max(p, SmallDrop * 1e3);
        return (10 * k * Math.Sqrt(p), 5 * k / Math.Sqrt(a));
    }

    private static State Solve(HydraulicNetwork network, NetworkNode source, double sourceHead)
    {
        var nodes = network.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var free = network.Nodes.Where(n => n.Id != source.Id).Select(n => n.Id).ToList();
        var index = free.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i, StringComparer.Ordinal);
        var head = network.Nodes.ToDictionary(n => n.Id, n => n.Id == source.Id ? sourceHead : 0.9 * sourceHead, StringComparer.Ordinal);
        var n = free.Count;

        double[] Residual(Dictionary<string, double> h)
        {
            // F_i = приток − отток по трубам − эмиттер.
            var f = new double[n];
            foreach (var p in network.Pipes)
            {
                var q = PipeFlow(p, h[p.From] - h[p.To]).Q;
                if (index.TryGetValue(p.To, out var t))
                {
                    f[t] += q;
                }

                if (index.TryGetValue(p.From, out var s))
                {
                    f[s] -= q;
                }
            }

            foreach (var id in free)
            {
                if (nodes[id].Emitter is { } k)
                {
                    f[index[id]] -= EmitterFlow(k.Value, h[id] - (nodes[id].Elevation.Meters / 100.0)).Q;
                }
            }

            return f;
        }

        var converged = false;
        var residual = Residual(head);
        for (var iteration = 0; iteration < 100; iteration++)
        {
            if (residual.Max(Math.Abs) < FlowTolerance)
            {
                converged = true;
                break;
            }

            // Матрица A = −∂F/∂h: взвешенный лапласиан труб + производные эмиттеров (симметричная, положительно определённая).
            var diag = new double[n];
            var off = new List<(int I, int J, double G)>();
            foreach (var p in network.Pipes)
            {
                var g = PipeFlow(p, head[p.From] - head[p.To]).G;
                var hasFrom = index.TryGetValue(p.From, out var a);
                var hasTo = index.TryGetValue(p.To, out var b);
                if (hasFrom)
                {
                    diag[a] += g;
                }

                if (hasTo)
                {
                    diag[b] += g;
                }

                if (hasFrom && hasTo)
                {
                    off.Add((a, b, g));
                }
            }

            foreach (var id in free)
            {
                if (nodes[id].Emitter is { } k)
                {
                    diag[index[id]] += EmitterFlow(k.Value, head[id] - (nodes[id].Elevation.Meters / 100.0)).G;
                }
            }

            var step = ConjugateGradient(diag, off, residual);

            // Демпфирование: шаг уменьшается, пока невязка не убывает.
            var norm = residual.Sum(x => x * x);
            var lambda = 1.0;
            Dictionary<string, double> trial;
            double[] trialResidual;
            do
            {
                trial = new Dictionary<string, double>(head, StringComparer.Ordinal);
                foreach (var id in free)
                {
                    trial[id] += lambda * step[index[id]];
                }

                trialResidual = Residual(trial);
                lambda /= 2;
            }
            while (trialResidual.Sum(x => x * x) > norm && lambda > 1e-6);

            (head, residual) = (trial, trialResidual);
        }

        return new State(head, nodes, converged || residual.Max(Math.Abs) < FlowTolerance);
    }

    // A·x = b для A = diag − Σ g·(e_i e_jᵀ + e_j e_iᵀ); предобуславливатель — диагональ.
    private static double[] ConjugateGradient(double[] diag, List<(int I, int J, double G)> off, double[] b)
    {
        var n = b.Length;
        double[] Multiply(double[] v)
        {
            var r = new double[n];
            for (var i = 0; i < n; i++)
            {
                r[i] = diag[i] * v[i];
            }

            foreach (var (i, j, g) in off)
            {
                r[i] -= g * v[j];
                r[j] -= g * v[i];
            }

            return r;
        }

        var x = new double[n];
        var res = (double[])b.Clone();
        var z = res.Select((v, i) => v / diag[i]).ToArray();
        var p = (double[])z.Clone();
        var rz = Dot(res, z);
        for (var k = 0; k < 10 * n && Math.Sqrt(Dot(res, res)) > 1e-15; k++)
        {
            var ap = Multiply(p);
            var alpha = rz / Dot(p, ap);
            for (var i = 0; i < n; i++)
            {
                x[i] += alpha * p[i];
                res[i] -= alpha * ap[i];
            }

            for (var i = 0; i < n; i++)
            {
                z[i] = res[i] / diag[i];
            }

            var rzNext = Dot(res, z);
            var beta = rzNext / rz;
            rz = rzNext;
            for (var i = 0; i < n; i++)
            {
                p[i] = z[i] + (beta * p[i]);
            }
        }

        return x;
    }

    private static double Dot(double[] a, double[] b)
    {
        var s = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            s += a[i] * b[i];
        }

        return s;
    }
}
