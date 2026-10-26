namespace WaterAuto.Calc.Layout;

/// <summary>
/// Отрезок оси, на котором стоят оросители одной линии (ряда), мм. На концах — стена или разрез между зонами.
/// </summary>
/// <param name="EndLimitLo">Наибольшее расстояние от начала отрезка до первой линии.</param>
/// <param name="MinOffsetLo">Наименьшее расстояние от начала отрезка до любой линии.</param>
internal readonly record struct AxisSpan(double Lo, double Hi, double EndLimitLo, double EndLimitHi, double MinOffsetLo, double MinOffsetHi);

/// <summary>
/// Положения линий оросителей по одной оси: наименьшее число линий, при котором на каждом отрезке
/// первая и последняя линии не дальше предела от концов, соседние — не дальше шага и не ближе наименьшего шага.
/// Перебор по сетке 10 мм (динамическое программирование). При равном числе — самый равномерный вариант:
/// наибольший из наименьших «шагов», где шаг у конца отрезка — удвоенное расстояние до него (у стены — полшага);
/// затем наименьшая сумма квадратов тех же величин.
/// </summary>
internal static class LineSolver
{
    public const double StepMm = 10;

    private const double Eps = 1e-6;

    public static IReadOnlyList<double>? Solve(IReadOnlyList<AxisSpan> spans, double maxSpacing, double minSpacing, IReadOnlyList<(double Lo, double Hi)> forbidden)
    {
        var result = new List<double>();
        foreach (var component in Components(spans))
        {
            var lines = SolveComponent(component, maxSpacing, minSpacing, forbidden);
            if (lines is null)
            {
                return null;
            }

            result.AddRange(lines);
        }

        return result;
    }

    // Отрезки, которые перекрываются или касаются, решаются вместе.
    private static IEnumerable<List<AxisSpan>> Components(IReadOnlyList<AxisSpan> spans)
    {
        var sorted = spans.OrderBy(s => s.Lo).ThenBy(s => s.Hi).ToList();
        var current = new List<AxisSpan>();
        var hi = double.NegativeInfinity;
        foreach (var s in sorted)
        {
            if (current.Count > 0 && s.Lo > hi + Eps)
            {
                yield return current;
                current = [];
            }

            current.Add(s);
            hi = Math.Max(hi, s.Hi);
        }

        if (current.Count > 0)
        {
            yield return current;
        }
    }

    private static List<double>? SolveComponent(List<AxisSpan> spans, double s, double m, IReadOnlyList<(double Lo, double Hi)> forbidden)
    {
        var lo = Math.Ceiling(spans.Min(x => x.Lo) / StepMm) * StepMm;
        var n = (int)Math.Floor((spans.Max(x => x.Hi) - lo) / StepMm) + 1;
        var x = new double[n];
        var inside = new int[n][];
        var valid = new bool[n];
        for (var k = 0; k < n; k++)
        {
            x[k] = lo + (k * StepMm);
            var at = x[k];
            inside[k] = Enumerable.Range(0, spans.Count).Where(i => spans[i].Lo + Eps < at && at < spans[i].Hi - Eps).ToArray();
            valid[k] = inside[k].Length > 0
                && inside[k].All(i => at >= spans[i].Lo + spans[i].MinOffsetLo - Eps && at <= spans[i].Hi - spans[i].MinOffsetHi + Eps)
                && !forbidden.Any(f => at >= f.Lo - Eps && at <= f.Hi + Eps);
        }

        // Наименьший правый конец среди отрезков, начинающихся не левее точки, — чтобы не перепрыгнуть отрезок целиком.
        var minHiFrom = new double[n];
        for (var k = 0; k < n; k++)
        {
            var at = x[k];
            minHiFrom[k] = spans.Where(sp => sp.Lo >= at - Eps).Select(sp => sp.Hi).DefaultIfEmpty(double.PositiveInfinity).Min();
        }

        var minHi = spans.Min(sp => sp.Hi);
        var maxLo = spans.Max(sp => sp.Lo);
        var maxJump = Math.Max(s, 2 * spans.Max(sp => Math.Max(sp.EndLimitLo, sp.EndLimitHi)));
        var window = (int)Math.Floor((maxJump + Eps) / StepMm);
        var count = new int[n];
        var worst = new double[n];
        var cost = new double[n];
        var prev = new int[n];
        Array.Fill(count, int.MaxValue);
        Array.Fill(prev, -1);

        for (var q = 0; q < n; q++)
        {
            if (!valid[q])
            {
                continue;
            }

            var xq = x[q];
            if (xq < minHi - Eps && inside[q].All(i => xq - spans[i].Lo <= spans[i].EndLimitLo + Eps))
            {
                count[q] = 1;
                worst[q] = inside[q].Min(i => 2 * (xq - spans[i].Lo));
                cost[q] = inside[q].Sum(i => Sq(2 * (xq - spans[i].Lo)));
            }

            for (var p = Math.Max(0, q - window); p < q; p++)
            {
                if (count[p] == int.MaxValue || xq - x[p] < m - Eps || minHiFrom[p] <= xq + Eps)
                {
                    continue;
                }

                var step = Transition(spans, inside[p], inside[q], x[p], xq, s);
                if (step is null)
                {
                    continue;
                }

                var c = count[p] + 1;
                var w = Math.Min(worst[p], step.Value.Worst);
                var total = cost[p] + step.Value.Cost;
                if (Better(c, w, total, count[q], worst[q], cost[q]))
                {
                    count[q] = c;
                    worst[q] = w;
                    cost[q] = total;
                    prev[q] = p;
                }
            }
        }

        var best = -1;
        var bestWorst = 0.0;
        var bestCost = double.PositiveInfinity;
        for (var p = 0; p < n; p++)
        {
            var xp = x[p];
            if (count[p] == int.MaxValue || xp <= maxLo + Eps || !inside[p].All(i => spans[i].Hi - xp <= spans[i].EndLimitHi + Eps))
            {
                continue;
            }

            var w = Math.Min(worst[p], inside[p].Min(i => 2 * (spans[i].Hi - xp)));
            var total = cost[p] + inside[p].Sum(i => Sq(2 * (spans[i].Hi - xp)));
            if (best < 0 || Better(count[p], w, total, count[best], bestWorst, bestCost))
            {
                best = p;
                bestWorst = w;
                bestCost = total;
            }
        }

        if (best < 0)
        {
            return null;
        }

        var lines = new List<double>();
        for (var k = best; k >= 0; k = prev[k])
        {
            lines.Add(x[k]);
        }

        lines.Reverse();
        return lines;
    }

    // Меньше линий; затем больше наименьший шаг (с точностью до шага сетки); затем меньше сумма квадратов.
    private static bool Better(int count, double worst, double cost, int otherCount, double otherWorst, double otherCost) =>
        count != otherCount ? count < otherCount
        : Math.Abs(worst - otherWorst) > StepMm / 2 ? worst > otherWorst
        : cost < otherCost - Eps;

    // Наименьший «шаг» и стоимость перехода p → q или null, если переход нарушает правила какого-то отрезка.
    private static (double Worst, double Cost)? Transition(List<AxisSpan> spans, int[] atP, int[] atQ, double p, double q, double s)
    {
        var shared = false;
        var worst = double.PositiveInfinity;
        var cost = 0.0;
        foreach (var i in atP)
        {
            if (atQ.Contains(i))
            {
                if (q - p > s + Eps)
                {
                    return null;
                }

                shared = true;
            }
            else if (spans[i].Hi - p > spans[i].EndLimitHi + Eps)
            {
                return null;
            }
            else
            {
                worst = Math.Min(worst, 2 * (spans[i].Hi - p));
                cost += Sq(2 * (spans[i].Hi - p));
            }
        }

        foreach (var i in atQ)
        {
            if (atP.Contains(i))
            {
                continue;
            }

            if (q - spans[i].Lo > spans[i].EndLimitLo + Eps)
            {
                return null;
            }

            worst = Math.Min(worst, 2 * (q - spans[i].Lo));
            cost += Sq(2 * (q - spans[i].Lo));
        }

        return shared ? (Math.Min(worst, q - p), cost + Sq(q - p)) : (worst, cost);
    }

    private static double Sq(double v) => v * v;
}
