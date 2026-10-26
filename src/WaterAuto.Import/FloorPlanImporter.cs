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
    public const string PumpRoomLayer = "ПТ_НС";
    public const string PumpInletLayer = "ПТ_НС_ВВОД";
    public const string PumpOutletLayer = "ПТ_НС_ВЫПУСК";
    public const string PumpTruckLayer = "ПТ_НС_ГМ";
    public const string BuildingInletLayer = "ПТ_ВВОД_ЗД";

    private static readonly string[] KnownLayers = [ContourLayer, ColumnLayer, BeamLayer, DuctLayer, NoSprinklerLayer, InletLayer, PumpRoomLayer, PumpInletLayer, PumpOutletLayer, PumpTruckLayer, BuildingInletLayer];

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
        var pumpRooms = new List<(string Id, Polygon Outline)>();
        var doors = new List<PlanPoint>();
        var stationInlets = new List<PlanPoint>();
        var stationOutlets = new List<PlanPoint>();
        var truckInlets = new List<PlanPoint>();
        var buildingInlets = new List<PlanPoint>();
        var zoneTexts = new List<(PlanPoint Point, string Text, string Id)>();

        foreach (var entity in doc.Entities)
        {
            var layer = KnownLayer(entity.Layer?.Name);
            if (layer is null)
            {
                continue;
            }

            var id = Id(entity);

            // Объекты с нормалью (0, 0, −1) — зеркальные: их координаты в системе объекта (OCS), где ось X направлена против мировой.
            // Иные нормали (наклонные плоскости) не поддерживаются.
            var mirror = false;
            if (NormalOf(entity) is { } normal)
            {
                var flat = Math.Abs(normal.X) < 1e-9 && Math.Abs(normal.Y) < 1e-9;
                if (!flat)
                {
                    issues.Add(new Issue("import.tilted", IssueLevel.Error, $"Объект {entity.ObjectName} на слое {layer} лежит не в плоскости плана.", id, null, Anchor(entity)));
                    continue;
                }

                mirror = normal.Z < 0;
            }

            switch (layer, entity)
            {
                case (ContourLayer, TextEntity t):
                    texts.Add((W(P(t.InsertPoint), mirror), t.Value));
                    break;
                case (ContourLayer, MText t):
                    texts.Add((P(t.InsertPoint), t.PlainText));
                    break;
                case (NoSprinklerLayer, TextEntity t):
                    zoneTexts.Add((W(P(t.InsertPoint), mirror), t.Value, id));
                    break;
                case (NoSprinklerLayer, MText t):
                    zoneTexts.Add((P(t.InsertPoint), t.PlainText, id));
                    break;
                case (PumpRoomLayer, TextEntity or MText):
                    break;
                case (PumpRoomLayer, LwPolyline pl):
                    if (Closed(pl, id, layer, mirror, issues) is { } pumpRoom)
                    {
                        pumpRooms.Add((id, pumpRoom));
                    }

                    break;
                case (PumpRoomLayer, Point pt):
                    doors.Add(P(pt.Location));
                    break;
                case (PumpInletLayer, Point pt):
                    stationInlets.Add(P(pt.Location));
                    break;
                case (PumpOutletLayer, Point pt):
                    stationOutlets.Add(P(pt.Location));
                    break;
                case (BuildingInletLayer, Point pt):
                    buildingInlets.Add(P(pt.Location));
                    break;
                case (BuildingInletLayer, Insert ins):
                    buildingInlets.Add(W(P(ins.InsertPoint), mirror));
                    break;
                case (PumpTruckLayer, Point pt):
                    truckInlets.Add(P(pt.Location));
                    break;
                case (PumpInletLayer or PumpOutletLayer, Insert ins):
                    (layer == PumpInletLayer ? stationInlets : stationOutlets).Add(W(P(ins.InsertPoint), mirror));
                    break;
                case (_, TextEntity or MText or Dimension):
                    issues.Add(new Issue("import.annotationSkipped", IssueLevel.Warning, $"Надпись или размер на слое {layer} пропущены: имя помещения пишется на слое {ContourLayer}.", id, null, Anchor(entity)));
                    break;
                case (ContourLayer, LwPolyline pl):
                    if (Closed(pl, id, layer, mirror, issues) is { } contour)
                    {
                        contours.Add((id, contour));
                    }

                    break;
                case (ColumnLayer, LwPolyline pl):
                    if (Closed(pl, id, layer, mirror, issues) is { } column)
                    {
                        columns.Add(new Column(id, column));
                    }

                    break;
                case (ColumnLayer, Circle c):
                    columns.Add(new Column(id, CirclePolygon(W(P(c.Center), mirror), c.Radius)));
                    break;
                case (ColumnLayer, Insert ins):
                    if (BlockOutline(ins, id, mirror, issues) is { } blockColumn)
                    {
                        columns.Add(new Column(id, blockColumn));
                    }

                    break;
                case (BeamLayer, LwPolyline pl):
                    if (Closed(pl, id, layer, mirror, issues) is { } beam)
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
                    if (Closed(pl, id, layer, mirror, issues) is { } duct)
                    {
                        ducts.Add(new Duct(id, duct, Length.FromMillimeters(pl.Elevation)));
                    }

                    break;
                case (NoSprinklerLayer, LwPolyline pl):
                    if (Closed(pl, id, layer, mirror, issues) is { } zone)
                    {
                        zones.Add(new NoSprinklerZone(id, zone));
                    }

                    break;
                case (InletLayer, Point pt):
                    inlets.Add(new Inlet(id, P(pt.Location), null));
                    break;
                case (InletLayer, Insert ins):
                    inlets.Add(new Inlet(id, W(P(ins.InsertPoint), mirror), ins.Block?.Name));
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

        // Подпись «ПНС» / «Насосная» в зоне запрета делает зону насосной (если слоя ПТ_НС нет); прочие подписи — как раньше.
        foreach (var (point, text, textId) in zoneTexts)
        {
            var zone = zones.FirstOrDefault(z => z.Outline.Contains(point));
            if (zone is not null && pumpRooms.Count == 0 && (text.Contains("ПНС", StringComparison.OrdinalIgnoreCase) || text.Contains("насосн", StringComparison.OrdinalIgnoreCase)))
            {
                pumpRooms.Add((zone.Id, zone.Outline));
                issues.Add(new Issue("import.pumpRoomFromZone", IssueLevel.Info, $"Зона запрета с подписью «{text}» принята за помещение насосной станции; явно — слой {PumpRoomLayer}.", zone.Id, null, point));
            }
            else
            {
                issues.Add(new Issue("import.annotationSkipped", IssueLevel.Warning, $"Надпись или размер на слое {NoSprinklerLayer} пропущены: имя помещения пишется на слое {ContourLayer}.", textId, null, point));
            }
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

        var plan = issues.Any(i => i.Level == IssueLevel.Error) ? null : new FloorPlan(rooms, columns, beams, ducts, zones, inlets)
        {
            BuildingInlets = buildingInlets,

            // Дверь — точка слоя ПТ_НС на контуре или рядом с ним (не дальше 0,5 м).
            // Ввод и выпуски — точки слоёв ПТ_НС_ВВОД и ПТ_НС_ВЫПУСК на стене насосной (не дальше 0,5 м).
            PumpRooms = [.. pumpRooms.Select(r => new PumpRoom(r.Id, r.Outline, doors.Where(d => r.Outline.DistanceTo(d) <= 500).Select(d => (PlanPoint?)d).FirstOrDefault())
            {
                Inlets = [.. stationInlets.Where(p => r.Outline.DistanceTo(p) <= 500)],
                Outlets = [.. stationOutlets.Where(p => r.Outline.DistanceTo(p) <= 500)],
                TruckInlets = [.. truckInlets.Where(p => r.Outline.DistanceTo(p) <= 500)],
            })],
        };
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
        // Пустые слои (например, слои результатов из шаблона чертежа) ни на что не влияют — о них не предупреждаем.
        var used = doc.Entities.Select(e => e.Layer.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var layer in doc.Layers.Where(l => used.Contains(l.Name)))
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

    private static Polygon? Closed(LwPolyline pl, string id, string layer, bool mirror, List<Issue> issues)
    {
        if (ClosedOcs(pl, id, layer, issues) is not { } ocs)
        {
            return null;
        }

        return mirror ? new Polygon([.. ocs.Vertices.Select(v => W(v, mirror))]) : ocs;
    }

    // Контур в координатах объекта (OCS); дуги разбиты на хорды.
    private static Polygon? ClosedOcs(LwPolyline pl, string id, string layer, List<Issue> issues)
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

    // Колонна-блок: первая замкнутая полилиния (с дугами) или окружность блока;
    // мир = точка вставки + поворот·масштаб·(локальная − базовая точка блока), затем OCS вставки → мир.
    private static Polygon? BlockOutline(Insert ins, string id, bool mirror, List<Issue> issues)
    {
        var at = W(P(ins.InsertPoint), mirror);
        if (ins.ColumnCount > 1 || ins.RowCount > 1)
        {
            issues.Add(new Issue("import.minsert", IssueLevel.Error, "Массив блоков (MINSERT) на слое колонн не поддерживается: разбейте массив на отдельные вставки.", id, null, at));
            return null;
        }

        Polygon? local = null;
        foreach (var e in ins.Block?.Entities ?? Enumerable.Empty<Entity>())
        {
            if (e is Insert)
            {
                issues.Add(new Issue("import.nestedBlock", IssueLevel.Error, "Вложенные блоки на слое колонн не поддерживаются: взорвите блок.", id, null, at));
                return null;
            }

            local ??= e switch
            {
                LwPolyline pl when pl.IsClosed => ClosedOcs(pl, id, ColumnLayer, issues),
                Circle c => CirclePolygon(P(c.Center), c.Radius),
                _ => null,
            };
        }

        if (local is null)
        {
            issues.Add(new Issue("import.emptyBlock", IssueLevel.Error, $"В блоке «{ins.Block?.Name}» нет замкнутой полилинии или окружности сечения колонны.", id, null, at));
            return null;
        }

        var basePoint = ins.Block?.BlockEntity?.BasePoint ?? XYZ.Zero;
        double cos = Math.Cos(ins.Rotation), sin = Math.Sin(ins.Rotation);
        return new Polygon([.. local.Vertices.Select(v =>
        {
            double x = (v.XMm - basePoint.X) * ins.XScale, y = (v.YMm - basePoint.Y) * ins.YScale;
            return W(new PlanPoint(ins.InsertPoint.X + (x * cos) - (y * sin), ins.InsertPoint.Y + (x * sin) + (y * cos)), mirror);
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
                issues.Add(new Issue("import.partlyOutside", IssueLevel.Info, $"{kind} частично выходит за контур помещения — будет обрезана по контуру.", id, null, outline.Centroid));
            }
        }
    }

    private static string Expected(string layer) => layer switch
    {
        ColumnLayer => "замкнутая полилиния, окружность или блок",
        InletLayer => "точка или блок",
        PumpRoomLayer => "замкнутая полилиния (контур насосной) и точка (дверь)",
        PumpInletLayer or PumpOutletLayer => "точка или блок на стене насосной",
        PumpTruckLayer => "точка на наружной стене насосной",
        BuildingInletLayer => "точка или блок на наружной стене (ввод водопровода в здание)",
        ContourLayer => "замкнутая полилиния (и текст с именем)",
        _ => "замкнутая полилиния",
    };

    private static string Id(Entity e) => "H" + e.Handle.ToString("X", CultureInfo.InvariantCulture);

    private static PlanPoint P(XY v) => new(v.X, v.Y);

    // OCS → мир для нормали (0, 0, ±1): при −1 ось X объекта противоположна мировой (правило произвольной оси AutoCAD).
    private static PlanPoint W(PlanPoint ocs, bool mirror) => mirror ? new PlanPoint(-ocs.XMm, ocs.YMm) : ocs;

    private static XYZ? NormalOf(Entity e) => e switch
    {
        LwPolyline pl => pl.Normal,
        Circle c => c.Normal,
        Insert i => i.Normal,
        TextEntity t => t.Normal,
        _ => null,
    };

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
