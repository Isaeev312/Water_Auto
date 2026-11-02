namespace WaterAuto.Core;

public enum RouteNodeKind
{
    Sprinkler,
    Tee,
    Bend,
    Inlet,
}

/// <summary>Роль участка: рядок (распределительный), питающий или подводящий от ввода.</summary>
public enum RouteSegmentRole
{
    Row,
    Main,
    Supply,
}

/// <summary>Узел трассы. Для оросителя SprinklerId — ID из раскладки.</summary>
public sealed record RouteNode(string Id, RouteNodeKind Kind, PlanPoint Location, string? SprinklerId = null);

/// <summary>
/// Участок трассы. From — ближе к вводу. Dn — начальный диаметр (табл. Б.4), Fed — число оросителей за участком.
/// </summary>
public sealed record RouteSegment(string Id, string From, string To, RouteSegmentRole Role, Length Length, int Fed, int Dn);

/// <summary>
/// Трасса одного помещения: дерево от ввода до оросителей (Segments) и замыкающие участки колец (Closures) —
/// участки, которые соединяют два узла дерева и образуют кольцо (этап 4, ADR 0011).
/// </summary>
public sealed record PipeRoute(string RoomId, string InletId, IReadOnlyList<RouteNode> Nodes, IReadOnlyList<RouteSegment> Segments, IReadOnlyList<RouteSegment>? Closures = null)
{
    /// <summary>Замыкающие участки колец; пусто — тупиковая сеть.</summary>
    public IReadOnlyList<RouteSegment> Loops => Closures ?? [];
}

public sealed record RoutingResult(IReadOnlyList<PipeRoute> Routes, IReadOnlyList<Issue> Issues);

/// <summary>Опора (держатель) трубопровода: место на плане, DN трубы и участок.</summary>
public sealed record SupportPoint(PlanPoint At, int Dn, string SegmentId);

/// <summary>Вид соединения стальных трубопроводов (п. 6.7.2.1 СП 485).</summary>
public enum JointKind
{
    /// <summary>Сварные (фасонные части — приварные).</summary>
    Welded,

    /// <summary>Трубопроводные разъёмные (грувлочные) муфты по ГОСТ Р 51737.</summary>
    Grooved,

    /// <summary>Резьбовые (соединительные части из ковкого чугуна).</summary>
    Threaded,
}

/// <summary>Изделие каталога муфт: наружный диаметр трубы (и меньший — у переходных), мм, и обозначение изготовителя.</summary>
public sealed record GroovedItem(double Od, double? Od2, string Designation);

/// <summary>
/// Каталог муфт и фасонных частей под муфту изготовителя: изделия по видам (coupling, elbow90, tee, cross, teeReducing,
/// crossReducing, reducer); подбор — по наружному диаметру трубы с допуском, при равной близости — первое в списке.
/// </summary>
public sealed record GroovedCatalog(double ToleranceMm, IReadOnlyDictionary<string, IReadOnlyList<GroovedItem>> Items)
{
    public string? Find(string kind, double od, double? od2 = null)
    {
        if (!Items.TryGetValue(kind, out var list))
        {
            return null;
        }

        return list
            .Where(i => Math.Abs(i.Od - od) <= ToleranceMm + 1e-9 && (od2 is null ? i.Od2 is null : i.Od2 is { } b && Math.Abs(b - od2.Value) <= ToleranceMm + 1e-9))
            .Select((i, k) => (i, k, d: Math.Abs(i.Od - od) + (od2 is { } o2 && i.Od2 is { } b2 ? Math.Abs(b2 - o2) : 0)))
            .OrderBy(x => x.d).ThenBy(x => x.k)
            .Select(x => x.i.Designation)
            .FirstOrDefault();
    }
}
