using System.Globalization;
using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Types.Units;
using CSMath;
using WaterAuto.Core;

namespace WaterAuto.Import;

/// <summary>Результат импорта: план (null при ошибках) и замечания с координатами.</summary>
public sealed record ImportResult(FloorPlan? Plan, IReadOnlyList<Issue> Issues)
{
    public bool HasErrors => Issues.Any(i => i.Level == IssueLevel.Error);
}

/// <summary>
/// Импорт плана этажа из DWG/DXF по соглашению о слоях (README §7, ARCHITECTURE §8).
/// Читается только пространство модели. Всё, что лежит на слоях ПТ_ и не поддержано, — замечание, а не пропуск.
/// При ошибках импорт не продолжается: план = null.
/// </summary>
public static class FloorPlanImporter
{
    public const string ContourLayer = "ПТ_КОНТУР";
    public const string ColumnLayer = "ПТ_КОЛОННЫ";
    public const string BeamLayer = "ПТ_БАЛКИ";
    public const string DuctLayer = "ПТ_КОРОБА";
    public const string NoSprinklerLayer = "ПТ_ЗАПРЕТ";
    public const string InletLayer = "ПТ_ВВОД";

    private static readonly string[] KnownLayers = [ContourLayer, ColumnLayer, BeamLayer, DuctLayer, NoSprinklerLayer, InletLayer];

    // Шаг разбиения дуг полилиний (bulge) и окружностей колонн, радиан.
    private const double ArcStep = Math.PI / 18;

    static FloorPlanImporter()
    {
        // DXF R2000–R2004 русского AutoCAD хранят текст в кодовой странице $DWGCODEPAGE (ANSI_1251).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static ImportResult Import(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        CadDocument doc;
        try
        {
            doc = Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase)
                ? DwgReader.Read(path)
                : DxfReader.Read(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return new ImportResult(null, [new Issue("import.read", IssueLevel.Error, $"Файл не прочитан: {ex.Message}")]);
        }

        return Import(doc);
    }

    public static ImportResult Import(CadDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var issues = new List<Issue>();
        var units = doc.Header.InsUnits;
        if (units is not UnitsType.Millimeters and not UnitsType.Unitless)
        {
            issues.Add(new Issue("import.units", IssueLevel.Error, $"Единицы чертежа — {units}; нужны миллиметры (шаблон WaterAuto_layers.dwg)."));
            return new ImportResult(null, issues);
        }

        if (units == UnitsType.Unitless)
        {
            issues.Add(new Issue("import.unitsUnset", IssueLevel.Info, "Единицы чертежа не заданы — приняты миллиметры."));
        }

        WarnAboutLookalikeLayers(doc, issues);

        var contours = new List<(string Id, Polygon Polygon)>();
        var texts = new List<(PlanPoint Point, string Text)>();
        var columns = new List<Column>();
        var beams = new List<Beam>();
        var ducts = new List<Duct>();
        var zones = new List<NoSprinklerZone>();
        var inlets = new List<Inlet>();

        foreach (var entity in doc.Entities)
        {
            var layer = KnownLayer(entity.Layer?.Name);
            if (layer is null)
            {
                continue;
            }

            var id = Id(entity);
            switch (layer, entity)
            {
                case (ContourLayer, TextEntity t):
                    texts.Add((P(t.InsertPoint), t.Value));
                    break;
                case (ContourLayer, MText t):
                    texts.Add((P(t.InsertPoint), t.Value));
                    break;
                case (ContourLayer, LwPolyline pl):
                    if (Closed(pl, id, layer, issues) is { } contour)
                    {
                        contours.Add((id, contour));
                    }

                    break;
                case (ColumnLayer, LwPolyline pl):
                    if (Closed(pl, id, layer, issues) is { } column)
                    {
                        columns.Add(new Column(id, column));
                    }

                    break;
                case (ColumnLayer, Circle c):
                    columns.Add(new Column(id, CirclePolygon(P(c.Center), c.Radius)));
                    break;
                case (ColumnLayer, Insert ins):
                    if (BlockOutline(ins, id, issues) is { } blockColumn)
                    {
                        columns.Add(new Column(id, blockColumn));
                    }

                    break;
                case (BeamLayer, LwPolyline pl):
                    if (Closed(pl, id, layer, issues) is { } beam)
                    {
                        if (pl.Thickness <= 0)
                        {
                            issues.Add(new Issue("import.beamHeight", IssueLevel.Error,
                                "У балки толщина 0: задайте «Толщина» = высоте балки, мм. От неё зависит, нужны ли оросители между балками.",
                                id, "6.2.10", beam.Centroid));
                        }
                        else
                        {
                            beams.Add(new Beam(id, beam, Length.FromMillimeters(pl.Thickness), Length.FromMillimeters(pl.Elevation)));
                        }
                    }

                    break;
                case (DuctLayer, LwPolyline pl):
                    if (Closed(pl, id, layer, issues) is { } duct)
                    {
                        ducts.Add(new Duct(id, duct, Length.FromMillimeters(pl.Elevation)));
                    }

                    break;
                case (NoSprinklerLayer, LwPolyline pl):
                    if (Closed(pl, id, layer, issues) is { } zone)
                    {
                        zones.Add(new NoSprinklerZone(id, zone));
                    }

                    break;
                case (InletLayer, Point pt):
                    inlets.Add(new Inlet(id, P(pt.Location), null));
                    break;
                case (InletLayer, Insert ins):
                    inlets.Add(new Inlet(id, P(ins.InsertPoint), ins.Block?.Name));
                    break;
                default:
                    issues.Add(new Issue("import.unsupportedEntity", IssueLevel.Error,
                        $"Объект {entity.ObjectName} на слое {layer} не поддерживается: на этом слое нужна {Expected(layer)}.",
                        id, null, Anchor(entity)));
                    break;
            }
        }

        if (contours.Count == 0)
        {
            issues.Add(new Issue("import.noContour", IssueLevel.Error, $"На слое {ContourLayer} нет ни одного замкнутого контура помещения."));
        }

        if (inlets.Count == 0)
        {
            issues.Add(new Issue("import.noInlet", IssueLevel.Error,
                $"Нет точки подключения: поставьте точку или блок на слое {InletLayer} в месте стояка или узла управления."));
        }

        CheckContours(contours, issues);
        var rooms = NameRooms(contours, texts, issues);
        var polygons = contours.Select(c => c.Polygon).ToList();
        CheckInside(columns.Select(c => (c.Id, c.Outline, "Колонна")), polygons, issues);
        CheckInside(beams.Select(b => (b.Id, b.Outline, "Балка")), polygons, issues);
        CheckInside(ducts.Select(d => (d.Id, d.Outline, "Короб")), polygons, issues);
        CheckInside(zones.Select(z => (z.Id, z.Outline, "Зона запрета")), polygons, issues);
        foreach (var inlet in inlets.Where(i => !polygons.Any(p => p.Contains(i.Location))))
        {
            issues.Add(new Issue("import.inletOutside", IssueLevel.Warning, "Точка подключения вне контуров помещений.", inlet.Id, null, inlet.Location));
        }

        var plan = issues.Any(i => i.Level == IssueLevel.Error) ? null : new FloorPlan(rooms, columns, beams, ducts, zones, inlets);
        return new ImportResult(plan, issues);
    }

    private static string? KnownLayer(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var upper = name.Trim().ToUpperInvariant();
        return KnownLayers.FirstOrDefault(l => l == upper);
    }

    // Слой «почти ПТ_»: латинские буквы вместо кириллических или опечатка — пользователь иначе увидит только «нет ПТ_ВВОД».
    private static void WarnAboutLookalikeLayers(CadDocument doc, List<Issue> issues)
    {
        foreach (var layer in doc.Layers)
        {
            var upper = layer.Name.Trim().ToUpperInvariant();
            var normalized = upper.Replace('P', 'Р').Replace('T', 'Т').Replace('K', 'К').Replace('O', 'О').Replace('A', 'А').Replace('B', 'В').Replace('E', 'Е');
            if (KnownLayer(upper) is null && (normalized.StartsWith("ПТ", StringComparison.Ordinal) || KnownLayers.Contains(normalized)))
            {
                var hint = KnownLayers.Contains(normalized) ? $" Похоже на {normalized}: проверьте латинские буквы." : "";
                issues.Add(new Issue("import.unknownLayer", IssueLevel.Warning, $"Слой «{layer.Name}» не входит в соглашение о слоях и не читается.{hint}"));
            }
        }
    }

    private static Polygon? Closed(LwPolyline pl, string id, string layer, List<Issue> issues)
    {
        var vertices = pl.Vertices.ToList();
        if (vertices.Count < 2)
        {
            issues.Add(new Issue("import.degenerate", IssueLevel.Error, $"Полилиния на слое {layer} из одной вершины.", id, null, vertices.Count > 0 ? P(vertices[0].Location) : null));
            return null;
        }

        var closedByVertex = Distance(P(vertices[0].Location), P(vertices[^1].Location)) <= Polygon.ToleranceMm;
        if (!pl.IsClosed && !closedByVertex)
        {
            issues.Add(new Issue("import.notClosed", IssueLevel.Error, $"Полилиния на слое {layer} не замкнута: включите «Замкнуто».", id, null, P(vertices[0].Location)));
            return null;
        }

        if (!pl.IsClosed)
        {
            issues.Add(new Issue("import.closedByVertex", IssueLevel.Info, $"Полилиния на слое {layer} замкнута совпадающими вершинами без флага «Замкнуто» — принята как замкнутая.", id, null, P(vertices[0].Location)));
            vertices.RemoveAt(vertices.Count - 1);
        }

        var points = new List<PlanPoint>();
        for (var i = 0; i < vertices.Count; i++)
        {
            var a = P(vertices[i].Location);
            points.Add(a);
            var bulge = vertices[i].Bulge;
            if (Math.Abs(bulge) > 1e-9)
            {
                points.AddRange(ArcPoints(a, P(vertices[(i + 1) % vertices.Count].Location), bulge));
            }
        }

        // Проверка после разбиения дуг: две вершины с дугой (полукруг) — допустимый контур.
        if (points.Count < 3)
        {
            issues.Add(new Issue("import.degenerate", IssueLevel.Error, $"Полилиния на слое {layer} не образует площади.", id, null, points[0]));
            return null;
        }

        var polygon = new Polygon(points);
        if (polygon.IsSelfIntersecting())
        {
            issues.Add(new Issue("import.selfIntersecting", IssueLevel.Error, $"Контур на слое {layer} самопересекается.", id, null, points[0]));
            return null;
        }

        return polygon;
    }

    // Внутренние точки дуги между a и b с выпуклостью bulge = tg(θ/4), θ — центральный угол.
    private static IEnumerable<PlanPoint> ArcPoints(PlanPoint a, PlanPoint b, double bulge)
    {
        var theta = 4 * Math.Atan(bulge);
        double dx = b.XMm - a.XMm, dy = b.YMm - a.YMm;
        var chord = Math.Sqrt((dx * dx) + (dy * dy));
        var radius = chord / (2 * Math.Sin(Math.Abs(theta) / 2));
        double mx = (a.XMm + b.XMm) / 2, my = (a.YMm + b.YMm) / 2;
        var h = radius * Math.Cos(Math.Abs(theta) / 2) * Math.Sign(bulge);
        double cx = mx - (h * dy / chord), cy = my + (h * dx / chord);
        var start = Math.Atan2(a.YMm - cy, a.XMm - cx);
        var steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(theta) / ArcStep));
        for (var k = 1; k < steps; k++)
        {
            var angle = start + (theta * k / steps);
            yield return new PlanPoint(cx + (radius * Math.Cos(angle)), cy + (radius * Math.Sin(angle)));
        }
    }

    private static Polygon CirclePolygon(PlanPoint center, double radius)
    {
        var n = (int)Math.Round(2 * Math.PI / ArcStep);
        return new Polygon([.. Enumerable.Range(0, n).Select(k => new PlanPoint(
            center.XMm + (radius * Math.Cos(2 * Math.PI * k / n)), center.YMm + (radius * Math.Sin(2 * Math.PI * k / n))))]);
    }

    // Колонна-блок: первая замкнутая полилиния или окружность блока с учётом точки вставки, масштаба и поворота.
    private static Polygon? BlockOutline(Insert ins, string id, List<Issue> issues)
    {
        Polygon? local = null;
        foreach (var e in ins.Block?.Entities ?? Enumerable.Empty<Entity>())
        {
            if (e is Insert)
            {
                issues.Add(new Issue("import.nestedBlock", IssueLevel.Error, "Вложенные блоки на слое колонн не поддерживаются: взорвите блок.", id, null, P(ins.InsertPoint)));
                return null;
            }

            local ??= e switch
            {
                LwPolyline pl when pl.IsClosed && pl.Vertices.Count >= 3 => new Polygon([.. pl.Vertices.Select(v => P(v.Location))]),
                Circle c => CirclePolygon(P(c.Center), c.Radius),
                _ => null,
            };
        }

        if (local is null)
        {
            issues.Add(new Issue("import.emptyBlock", IssueLevel.Error, $"В блоке «{ins.Block?.Name}» нет замкнутой полилинии или окружности сечения колонны.", id, null, P(ins.InsertPoint)));
            return null;
        }

        double cos = Math.Cos(ins.Rotation), sin = Math.Sin(ins.Rotation);
        return new Polygon([.. local.Vertices.Select(v =>
        {
            double x = v.XMm * ins.XScale, y = v.YMm * ins.YScale;
            return new PlanPoint(ins.InsertPoint.X + (x * cos) - (y * sin), ins.InsertPoint.Y + (x * sin) + (y * cos));
        })]);
    }

    private static void CheckContours(List<(string Id, Polygon Polygon)> contours, List<Issue> issues)
    {
        for (var i = 0; i < contours.Count; i++)
        {
            for (var j = i + 1; j < contours.Count; j++)
            {
                var relation = contours[i].Polygon.RelationTo(contours[j].Polygon);
                if (relation == PolygonRelation.Intersecting)
                {
                    issues.Add(new Issue("import.contoursIntersect", IssueLevel.Error, "Контуры помещений пересекаются.", contours[i].Id, null, contours[i].Polygon.Centroid));
                }
                else if (relation != PolygonRelation.Disjoint)
                {
                    issues.Add(new Issue("import.contourNested", IssueLevel.Error,
                        "Контур помещения лежит внутри другого: каждое помещение — отдельный контур, вырезы — слой ПТ_ЗАПРЕТ.", contours[i].Id, null, contours[i].Polygon.Centroid));
                }
            }
        }
    }

    private static List<Room> NameRooms(List<(string Id, Polygon Polygon)> contours, List<(PlanPoint Point, string Text)> texts, List<Issue> issues)
    {
        var rooms = new List<Room>();
        for (var i = 0; i < contours.Count; i++)
        {
            var (id, polygon) = contours[i];
            var inside = texts.Where(t => polygon.Contains(t.Point)).ToList();
            var name = inside.Count > 0 ? inside[0].Text.Trim() : string.Create(CultureInfo.InvariantCulture, $"Помещение {i + 1}");
            if (inside.Count == 0)
            {
                issues.Add(new Issue("import.roomUnnamed", IssueLevel.Info, $"В контуре нет текста с именем — принято «{name}».", id, null, polygon.Centroid));
            }
            else if (inside.Count > 1)
            {
                issues.Add(new Issue("import.roomManyNames", IssueLevel.Warning, $"В контуре несколько текстов — взят первый: «{name}».", id, null, polygon.Centroid));
            }

            rooms.Add(new Room(id, name, polygon));
        }

        return rooms;
    }

    // Целиком вне всех контуров — ошибка; частично (балка заходит в стену, колонна в стене) — предупреждение.
    private static void CheckInside(IEnumerable<(string Id, Polygon Outline, string Kind)> objects, List<Polygon> contours, List<Issue> issues)
    {
        foreach (var (id, outline, kind) in objects)
        {
            var relations = contours.Select(outline.RelationTo).ToList();
            if (relations.Contains(PolygonRelation.FirstInsideSecond))
            {
                continue;
            }

            if (relations.All(r => r == PolygonRelation.Disjoint))
            {
                issues.Add(new Issue("import.outsideContour", IssueLevel.Error, $"{kind} вне контуров помещений.", id, null, outline.Centroid));
            }
            else
            {
                issues.Add(new Issue("import.partlyOutside", IssueLevel.Warning, $"{kind} частично выходит за контур помещения — будет обрезана по контуру.", id, null, outline.Centroid));
            }
        }
    }

    private static string Expected(string layer) => layer switch
    {
        ColumnLayer => "замкнутая полилиния, окружность или блок",
        InletLayer => "точка или блок",
        ContourLayer => "замкнутая полилиния (и текст с именем)",
        _ => "замкнутая полилиния",
    };

    private static string Id(Entity e) => "H" + e.Handle.ToString("X", CultureInfo.InvariantCulture);

    private static PlanPoint P(XY v) => new(v.X, v.Y);

    private static PlanPoint P(XYZ v) => new(v.X, v.Y);

    private static PlanPoint? Anchor(Entity e) => e switch
    {
        Line l => P(l.StartPoint),
        Circle c => P(c.Center),
        Point p => P(p.Location),
        Insert i => P(i.InsertPoint),
        LwPolyline pl when pl.Vertices.Count > 0 => P(pl.Vertices[0].Location),
        TextEntity t => P(t.InsertPoint),
        MText t => P(t.InsertPoint),
        _ => null,
    };

    private static double Distance(PlanPoint a, PlanPoint b) => Math.Sqrt(Math.Pow(a.XMm - b.XMm, 2) + Math.Pow(a.YMm - b.YMm, 2));
}
