using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Calc.Routing;

/// <summary>Ороситель для спецификации: наименование и марка из каталога или от пользователя.</summary>
public sealed record SprinklerSpec(string Name, string Designation, string? Supplier = null, string? Note = null);

/// <summary>Антикоррозионное покрытие труб для спецификации: наименование (состав покрытия) и обозначение; количество — площадь, м².</summary>
public sealed record CoatingSpec(string Name, string? Designation);

/// <summary>
/// Соединения сети для спецификации: вид по DN (<see cref="PipeJoints"/>), мерная длина труб, стандарты резьбовых соединительных
/// частей по началу наименования и стандарт разъёмных муфт.
/// </summary>
public sealed record JointSpec(IReadOnlyDictionary<int, JointKind> ByDn, double StockLengthM, IReadOnlyDictionary<string, string> ThreadedStandards, string CouplingStandard);

/// <summary>
/// Ведомость материалов по трассе: оросители и запас (п. 6.1.15), трубы по DN сортамента с длинами,
/// фасонные части по геометрии трассы (отводы, тройники, крестовины, переходы, подключения оросителей),
/// площадь наружной поверхности труб под антикоррозионное покрытие.
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

    /// <param name="fittingStandard">Стандарты приварных деталей по началу наименования («Отвод» → ГОСТ 17375-2001, «Тройник» → ГОСТ 17376-2001,
    /// «Переход» → ГОСТ 17378-2001); null — не указываются.</param>
    /// <param name="steelDensity">Плотность стали для теоретической массы 1 м трубы, т/м³; null — масса не указывается.</param>
    /// <param name="coating">Покрытие труб; null — строка покрытия не добавляется.</param>
    public static MaterialTakeoff Build(IReadOnlyList<PipeRoute> routes, int installedSprinklers, INormsProvider norms, string pipeStandard, SprinklerSpec sprinkler, IReadOnlyDictionary<string, string>? fittingStandard = null, double? steelDensity = null, IReadOnlyList<PipeSize>? sizes = null, CoatingSpec? coating = null, JointSpec? joints = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(sprinkler);
        var series = new PipeSeries(norms, pipeStandard, sizes);
        var items = new List<TakeoffItem>
        {
            new(TakeoffGroup.Sprinklers, sprinkler.Name, sprinkler.Designation, installedSprinklers, "шт.", sprinkler.Note, Supplier: sprinkler.Supplier),
            new(TakeoffGroup.Sprinklers, $"{sprinkler.Name} (запас)", sprinkler.Designation, SpareSprinklers(norms, installedSprinklers), "шт.", "запас по п. 6.1.15 СП 485", Supplier: sprinkler.Supplier),
        };

        var pipes = new Dictionary<int, (SeriesPipe Pipe, double Meters)>();
        var fittings = new Dictionary<string, int>(StringComparer.Ordinal);
        var couplings = new SortedDictionary<int, int>();
        var straightCouplings = new SortedDictionary<int, int>();
        JointKind Kind(int dn) => joints?.ByDn.GetValueOrDefault(dn, JointKind.Welded) ?? JointKind.Welded;

        // Наименование фасонной части по виду соединения её концов: один вид — приварная как есть, под муфту — с пометкой,
        // на резьбе — соединительная часть из ковкого чугуна; концы разного вида (граница муфт и резьбы, сварки и муфт) —
        // с перечнем концов, чтобы деталь заказывалась переходной.
        static string KindText(JointKind k) => k switch
        {
            JointKind.Grooved => "под муфту",
            JointKind.Threaded => "резьба",
            _ => "под приварку",
        };
        string Named(string name, IReadOnlyList<int> dns)
        {
            var ends = dns.Distinct().OrderByDescending(d => d).ToList();
            if (ends.Select(Kind).Distinct().Count() > 1)
            {
                return $"{name} (концы: {string.Join(", ", ends.Select(d => $"DN{d} — {KindText(Kind(d))}"))})";
            }

            return Single(name, ends[0]);
        }

        string Single(string name, int dn) => Kind(dn) switch
        {
            JointKind.Grooved => name + " (под разъёмную муфту)",
            JointKind.Threaded => name.StartsWith("Отвод 90° ", StringComparison.Ordinal) ? "Угольник 90° " + name["Отвод 90° ".Length..]
                : name.StartsWith("Отвод под ороситель", StringComparison.Ordinal) ? "Угольник под ороситель" + name["Отвод под ороситель".Length..]
                : name.StartsWith("Переход ", StringComparison.Ordinal) ? "Муфта переходная " + name["Переход ".Length..]
                : name,
            _ => name,
        };
        void Fitting(string name, params int[] dns)
        {
            var n = Named(name, dns);
            fittings[n] = fittings.GetValueOrDefault(n) + 1;
        }

        foreach (var route in routes)
        {
            var nodes = route.Nodes.ToDictionary(n => n.Id);
            var incoming = route.Segments.ToDictionary(s => s.To);
            var outgoing = route.Segments.ToLookup(s => s.From);
            int Dn(RouteSegment s) => series.Pick(s.Dn)?.Dn ?? s.Dn;

            var closureAt = route.Loops.SelectMany(c => new[] { (Node: c.From, Seg: c), (Node: c.To, Seg: c) }).ToLookup(x => x.Node, x => x.Seg);
            foreach (var s in route.Segments.Concat(route.Loops))
            {
                if (series.Pick(s.Dn) is { } pipe)
                {
                    pipes[pipe.Dn] = (pipe, pipes.GetValueOrDefault(pipe.Dn).Meters + s.Length.Meters);

                    // Стыки труб по мерной длине; на муфтах — ещё по муфте на каждом конце участка (у фасонной части или ввода);
                    // в узле прямого прохода без фасонной части два конца соединяет одна муфта — учтено в цикле по узлам.
                    var pieces = joints is { StockLengthM: > 0 } j ? Math.Max(1, (int)Math.Ceiling((s.Length.Meters / j.StockLengthM) - 1e-9)) : 1;
                    if (Kind(pipe.Dn) == JointKind.Grooved)
                    {
                        couplings[pipe.Dn] = couplings.GetValueOrDefault(pipe.Dn) + 2 + (pieces - 1);
                    }
                    else if (Kind(pipe.Dn) == JointKind.Threaded && pieces > 1)
                    {
                        straightCouplings[pipe.Dn] = straightCouplings.GetValueOrDefault(pipe.Dn) + (pieces - 1);
                    }
                }
            }

            foreach (var node in route.Nodes.Where(n => incoming.ContainsKey(n.Id)))
            {
                var inSeg = incoming[node.Id];
                var inDn = Dn(inSeg);
                // Замыкающий участок кольца на узле — ещё одно ответвление (для фасонной части).
                var outs = outgoing[node.Id].Concat(closureAt[node.Id]).ToList();
                var straight = outgoing[node.Id].FirstOrDefault(o => Collinear(nodes, inSeg, o));

                // Концы фасонной части: приходящий DN (он же проход — переход на меньший DN ставится отдельно) и DN ответвлений.
                var branchDns = outs.Where(o => o != straight).Select(Dn).ToArray();
                if (node.Kind == RouteNodeKind.Sprinkler)
                {
                    Fitting(outs.Count == 0 ? $"Отвод под ороситель DN{inDn}" : $"Тройник под ороситель DN{inDn}", [inDn, .. branchDns]);
                }
                else if (outs.Count == 1 && straight is null)
                {
                    // Поворот со сменой DN (в любую сторону) — отвод большего DN и переход между DN.
                    var (big, small) = (Math.Max(inDn, Dn(outs[0])), Math.Min(inDn, Dn(outs[0])));
                    Fitting($"Отвод 90° DN{big}", big);
                    if (small < big)
                    {
                        Fitting($"Переход DN{big}×{small}", big, small);
                    }
                }
                else if (outs.Count == 2)
                {
                    // Равнопроходный тройник обозначается одним DN.
                    Fitting(outs.Min(Dn) == inDn ? $"Тройник DN{inDn}" : $"Тройник DN{inDn}×{outs.Min(Dn)}", [inDn, .. branchDns]);
                }
                else if (outs.Count >= 3)
                {
                    Fitting($"Крестовина DN{inDn}×{outs.Min(Dn)}", [inDn, .. branchDns]);
                }
                else if (outs.Count == 1 && straight is not null && Dn(straight) == inDn && series.Pick(inSeg.Dn) is not null)
                {
                    // Прямой проход без фасонной части: на муфтах — одна муфта на стык вместо двух концевых, на резьбе — прямая муфта.
                    if (Kind(inDn) == JointKind.Grooved)
                    {
                        couplings[inDn] = couplings.GetValueOrDefault(inDn) - 1;
                    }
                    else if (Kind(inDn) == JointKind.Threaded)
                    {
                        straightCouplings[inDn] = straightCouplings.GetValueOrDefault(inDn) + 1;
                    }
                }

                if (straight is not null && Dn(straight) < inDn)
                {
                    Fitting($"Переход DN{inDn}×{Dn(straight)}", inDn, Dn(straight));
                }
            }
        }

        // Теоретическая масса 1 м: m = π·(D − s)·s·ρ / 1000, кг/м (D, s — мм, ρ — т/м³).
        double? Mass(SeriesPipe pipe) => steelDensity is { } rho ? Math.Round(Math.PI * (pipe.OuterDiameterMm - pipe.WallMm) * pipe.WallMm * rho / 1000, 2) : null;
        items.AddRange(pipes.OrderBy(p => p.Key).Select(p => new TakeoffItem(
            TakeoffGroup.Pipes,
            string.Create(Ru, $"Трубы {p.Value.Pipe.PipeType} DN{p.Key}, Ø{p.Value.Pipe.OuterDiameterMm:0.##}×{p.Value.Pipe.WallMm:0.##} мм"),
            p.Value.Pipe.Standard,
            Math.Round(p.Value.Meters, 2),
            "м",
            Mass(p.Value.Pipe) is { } m ? string.Create(Ru, $"общая масса {m * p.Value.Meters:0.#} кг") : null,
            Mass(p.Value.Pipe))));
        if (coating is not null && pipes.Count > 0)
        {
            // Площадь наружной поверхности: π·D·L, м² (D — мм).
            var area = pipes.Values.Sum(p => Math.PI * p.Pipe.OuterDiameterMm / 1000 * p.Meters);
            items.Add(new TakeoffItem(TakeoffGroup.Materials, coating.Name, coating.Designation, Math.Round(area, 1), "м²", "по наружной поверхности труб"));
        }

        foreach (var (dn, n) in couplings)
        {
            fittings[$"Муфта трубопроводная разъёмная DN{dn}"] = n;
        }

        foreach (var (dn, n) in straightCouplings)
        {
            fittings[$"Муфта прямая DN{dn}"] = n;
        }

        // Стандарт: муфты — ГОСТ Р 51737; резьбовые части — по своим стандартам; под муфту — по каталогу изготовителя муфт; приварные — как раньше.
        TakeoffItem Row(string name, int count)
        {
            if (joints is not null && name.StartsWith("Муфта трубопроводная разъёмная", StringComparison.Ordinal))
            {
                return new TakeoffItem(TakeoffGroup.Fittings, name, joints.CouplingStandard, count, "шт.");
            }

            if (joints is not null && (name.EndsWith(" (под разъёмную муфту)", StringComparison.Ordinal) || (name.Contains(" (концы: ", StringComparison.Ordinal) && name.Contains("под муфту", StringComparison.Ordinal))))
            {
                return new TakeoffItem(TakeoffGroup.Fittings, name, null, count, "шт.", "по каталогу изготовителя муфт");
            }

            var threaded = joints?.ThreadedStandards.FirstOrDefault(x => name.StartsWith(x.Key, StringComparison.Ordinal)).Value;
            if (threaded is not null && Kind(Diameters(name).First) == JointKind.Threaded)
            {
                return new TakeoffItem(TakeoffGroup.Fittings, name, threaded, count, "шт.");
            }

            return fittingStandard?.FirstOrDefault(x => name.StartsWith(x.Key, StringComparison.Ordinal)).Value is { } standard
                ? new TakeoffItem(TakeoffGroup.Fittings, name, standard, count, "шт.")
                : new TakeoffItem(TakeoffGroup.Fittings, name, null, count, "шт.", "уточняется при оформлении узлов");
        }

        items.AddRange(fittings
            .OrderBy(f => FittingOrder(f.Key))
            .ThenBy(f => Diameters(f.Key).First)
            .ThenBy(f => Diameters(f.Key).Second)
            .Select(f => Row(f.Key, f.Value)));
        return new MaterialTakeoff(items, series.Issues.ToList());
    }

    // Группы фасонных частей (ГОСТ 21.110, п. 4.5): отводы, тройники, крестовины, переходы; подключения оросителей — в конце своей группы.
    private static int FittingOrder(string name) =>
        name.StartsWith("Отвод 90", StringComparison.Ordinal) || name.StartsWith("Угольник 90", StringComparison.Ordinal) ? 0
        : name.StartsWith("Отвод под ороситель", StringComparison.Ordinal) || name.StartsWith("Угольник под ороситель", StringComparison.Ordinal) ? 1
        : name.StartsWith("Тройник DN", StringComparison.Ordinal) ? 2
        : name.StartsWith("Тройник под ороситель", StringComparison.Ordinal) ? 3
        : name.StartsWith("Крестовина", StringComparison.Ordinal) ? 4
        : name.StartsWith("Переход", StringComparison.Ordinal) || name.StartsWith("Муфта переходная", StringComparison.Ordinal) ? 5
        : name.StartsWith("Муфта", StringComparison.Ordinal) ? 6
        : 7;

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
