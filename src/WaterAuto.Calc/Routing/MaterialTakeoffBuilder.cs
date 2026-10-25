using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Calc.Routing;

/// <summary>Ороситель для спецификации: наименование и марка из каталога или от пользователя.</summary>
public sealed record SprinklerSpec(string Name, string Designation);

/// <summary>
/// Ведомость материалов по трассе: оросители и запас (п. 6.1.15), трубы по DN сортамента с длинами,
/// фасонные части по геометрии трассы (отводы, тройники, крестовины, переходы, подключения оросителей).
/// Фасонные части — ориентировочно: при оформлении узлов их состав уточняется.
/// </summary>
public static class MaterialTakeoffBuilder
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Наименьший запас спринклерных оросителей по общему количеству (6.1.15).</summary>
    public static int SpareSprinklers(INormsProvider norms, int installed)
    {
        ArgumentNullException.ThrowIfNull(norms);
        var rows = norms.ByClause("6.1.15").ToList();
        var row = rows
            .Where(r => r.Values["upToTotal"] is { } upTo && installed <= upTo)
            .OrderBy(r => r.Values["upToTotal"])
            .FirstOrDefault() ?? rows.Single(r => r.Values["upToTotal"] is null);
        return (int)row.Values["sprinklerSpare"]!.Value;
    }

    public static MaterialTakeoff Build(IReadOnlyList<PipeRoute> routes, int installedSprinklers, INormsProvider norms, string pipeStandard, SprinklerSpec sprinkler)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(sprinkler);
        var series = new PipeSeries(norms, pipeStandard);
        var items = new List<TakeoffItem>
        {
            new(TakeoffGroup.Sprinklers, sprinkler.Name, sprinkler.Designation, installedSprinklers, "шт."),
            new(TakeoffGroup.Sprinklers, $"{sprinkler.Name} (запас)", sprinkler.Designation, SpareSprinklers(norms, installedSprinklers), "шт.", "запас по п. 6.1.15"),
        };

        var pipes = new Dictionary<int, (SeriesPipe Pipe, double Meters)>();
        var fittings = new Dictionary<string, int>(StringComparer.Ordinal);
        void Fitting(string name) => fittings[name] = fittings.GetValueOrDefault(name) + 1;

        foreach (var route in routes)
        {
            var nodes = route.Nodes.ToDictionary(n => n.Id);
            var incoming = route.Segments.ToDictionary(s => s.To);
            var outgoing = route.Segments.ToLookup(s => s.From);
            int Dn(RouteSegment s) => series.Pick(s.Dn)?.Dn ?? s.Dn;

            foreach (var s in route.Segments)
            {
                if (series.Pick(s.Dn) is { } pipe)
                {
                    pipes[pipe.Dn] = (pipe, pipes.GetValueOrDefault(pipe.Dn).Meters + s.Length.Meters);
                }
            }

            foreach (var node in route.Nodes.Where(n => incoming.ContainsKey(n.Id)))
            {
                var inSeg = incoming[node.Id];
                var inDn = Dn(inSeg);
                var outs = outgoing[node.Id].ToList();
                var straight = outs.FirstOrDefault(o => Collinear(nodes, inSeg, o));

                if (node.Kind == RouteNodeKind.Sprinkler)
                {
                    Fitting(outs.Count == 0 ? $"Отвод под ороситель DN{inDn}" : $"Тройник под ороситель DN{inDn}");
                }
                else if (outs.Count == 1 && straight is null)
                {
                    Fitting($"Отвод 90° DN{inDn}");
                }
                else if (outs.Count == 2)
                {
                    Fitting($"Тройник DN{inDn}×{outs.Min(Dn)}");
                }
                else if (outs.Count >= 3)
                {
                    Fitting($"Крестовина DN{inDn}×{outs.Min(Dn)}");
                }

                if (straight is not null && Dn(straight) < inDn)
                {
                    Fitting($"Переход DN{inDn}×{Dn(straight)}");
                }
            }
        }

        items.AddRange(pipes.OrderBy(p => p.Key).Select(p => new TakeoffItem(
            TakeoffGroup.Pipes,
            string.Create(Ru, $"Трубы {p.Value.Pipe.PipeType} DN{p.Key}, Ø{p.Value.Pipe.OuterDiameterMm:0.##}×{p.Value.Pipe.WallMm:0.##} мм"),
            pipeStandard,
            Math.Round(p.Value.Meters, 2),
            "м")));
        items.AddRange(fittings
            .OrderBy(f => FittingOrder(f.Key))
            .ThenBy(f => Diameters(f.Key).First)
            .ThenBy(f => Diameters(f.Key).Second)
            .Select(f => new TakeoffItem(TakeoffGroup.Fittings, f.Key, null, f.Value, "шт.", "уточняется при оформлении узлов")));
        return new MaterialTakeoff(items, series.Issues.ToList());
    }

    private static int FittingOrder(string name) =>
        name.StartsWith("Отвод 90", StringComparison.Ordinal) ? 0
        : name.StartsWith("Тройник DN", StringComparison.Ordinal) ? 1
        : name.StartsWith("Крестовина", StringComparison.Ordinal) ? 2
        : name.StartsWith("Переход", StringComparison.Ordinal) ? 3
        : 4;

    // DN в названии фасонной части: «Переход DN100×80» → (100, 80) — для сортировки по размеру, а не по тексту.
    private static (int First, int Second) Diameters(string name)
    {
        var dn = name.IndexOf("DN", StringComparison.Ordinal);
        var sizes = dn < 0 ? [] : System.Text.RegularExpressions.Regex.Matches(name[dn..], @"\d+").Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
        return (sizes.ElementAtOrDefault(0), sizes.ElementAtOrDefault(1));
    }

    // Продолжает ли участок b направление участка a (после узла a.To).
    private static bool Collinear(Dictionary<string, RouteNode> nodes, RouteSegment a, RouteSegment b)
    {
        var (p, q, r) = (nodes[a.From].Location, nodes[a.To].Location, nodes[b.To].Location);
        double ux = q.XMm - p.XMm, uy = q.YMm - p.YMm, vx = r.XMm - q.XMm, vy = r.YMm - q.YMm;
        var cross = (ux * vy) - (uy * vx);
        var dot = (ux * vx) + (uy * vy);
        return dot > 0 && Math.Abs(cross) <= 1e-6 * Math.Sqrt(((ux * ux) + (uy * uy)) * ((vx * vx) + (vy * vy)));
    }
}
