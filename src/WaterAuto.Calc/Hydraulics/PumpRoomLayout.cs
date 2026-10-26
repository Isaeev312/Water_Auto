using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

public enum StationItemKind
{
    PumpSet,
    Cabinet,
    ControlUnit,
    Jockey,
}

/// <summary>Оборудование для расстановки: вид, подпись, габариты (длина — вдоль стены, ширина — от стены), число однотипных.</summary>
public sealed record StationItem(StationItemKind Kind, string Label, Footprint Size, int Count = 1);

/// <summary>Поставленное оборудование: прямоугольник в плане, мм; Front — сторона обслуживания шкафа (единичный вектор).</summary>
public sealed record PlacedItem(StationItemKind Kind, string Label, double X0, double Y0, double X1, double Y1, (int X, int Y)? Front = null);

public sealed record PumpRoomPlan(PumpRoom Room, IReadOnlyList<PlacedItem> Items, IReadOnlyList<Issue> Issues, IReadOnlyList<string> Rules);

/// <summary>
/// Расстановка оборудования насосной станции в прямоугольном помещении с проходами п. 6.10.16 (значения — из данных норм):
/// модульная установка — до стен не менее moduleWall; между оборудованием — otherEquipment; между узлами управления
/// и от них до стены — controlUnits; перед электрощитом — switchboard; жокей-насос (DN до twinMaxDn) — допускается у стены.
/// У двери — свободная зона 1 × 1 м (правило программы). Перебор позиций с шагом 50 мм: установка — в угол дальше от двери,
/// остальное — ближе к установке. Не помещается — ошибка с размером помещения, при котором помещается.
/// </summary>
public static class PumpRoomLayout
{
    private const double Step = 50;
    private const double DoorZone = 1000;

    public static PumpRoomPlan Place(PumpRoom room, IReadOnlyList<StationItem> items, INormsProvider norms)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(norms);
        var pass = (norms.Find("sp485.p6_10_16.passages") ?? throw new InvalidOperationException("Нет sp485.p6_10_16.passages")).Values;
        double M(string key) => pass[key]!.Value * 1000;
        var (moduleWall, other, units, board) = (M("moduleWall"), M("otherEquipment"), M("controlUnits"), M("switchboard"));
        var rules = new List<string>
        {
            $"модульная насосная установка — стена: не менее {moduleWall / 1000:0.0} м",
            $"между выступающими частями оборудования: не менее {other / 1000:0.0} м",
            $"между узлами управления и от них до стены: не менее {units / 1000:0.0} м",
            $"перед электрическим щитом (шкафом управления): не менее {board / 1000:0.0} м",
        };

        var v = room.Outline.Vertices;
        var (w, h) = (v.Max(p => p.XMm) - v.Min(p => p.XMm), v.Max(p => p.YMm) - v.Min(p => p.YMm));
        var (ox, oy) = (v.Min(p => p.XMm), v.Min(p => p.YMm));
        if (v.Count != 4 || Math.Abs(room.Outline.AreaMm2 - (w * h)) > 1)
        {
            return new PumpRoomPlan(room, [], [new Issue("pumpRoom.shape", IssueLevel.Error, "Помещение насосной не прямоугольное (или повёрнуто): автоматическая расстановка выполняется только для прямоугольника по осям чертежа.", room.Id)], rules);
        }

        // Локальные координаты: (0, 0) — левый нижний угол помещения.
        var door = room.Door is { } d ? (X: d.XMm - ox, Y: d.YMm - oy) : ((double X, double Y)?)null;
        var placed = new List<PlacedItem>();
        var issues = new List<Issue>();

        double Gap(PlacedItem a, double x0, double y0, double x1, double y1)
        {
            var dx = Math.Max(0, Math.Max(a.X0 - x1, x0 - a.X1));
            var dy = Math.Max(0, Math.Max(a.Y0 - y1, y0 - a.Y1));
            return dx > 0 && dy > 0 ? Math.Sqrt((dx * dx) + (dy * dy)) : Math.Max(dx, dy);
        }

        bool DoorFree(double x0, double y0, double x1, double y1) =>
            door is not { } dd || x1 <= dd.X - DoorZone / 2 || x0 >= dd.X + DoorZone / 2 || y1 <= dd.Y - DoorZone || y0 >= dd.Y + DoorZone;

        // Шкаф: зона обслуживания перед ним тоже должна быть свободна.
        bool FrontFree(PlacedItem a, double x0, double y0, double x1, double y1) =>
            a.Front is not { } f || Gap(a with { X0 = Math.Min(a.X0, a.X0 + (f.X * board)), X1 = Math.Max(a.X1, a.X1 + (f.X * board)), Y0 = Math.Min(a.Y0, a.Y0 + (f.Y * board)), Y1 = Math.Max(a.Y1, a.Y1 + (f.Y * board)) }, x0, y0, x1, y1) > 0;

        PlacedItem? Best(StationItemKind kind, string label, double len, double wid, double wallGap, Func<StationItemKind, double> gapTo, Func<double, double, double, double, double> score, bool againstWall)
        {
            PlacedItem? best = null;
            var bestScore = double.MaxValue;
            foreach (var (a, b) in new[] { (len, wid), (wid, len) })
            {
                for (var x = wallGap; x + a <= w - wallGap + 1e-6; x += Step)
                {
                    for (var y = wallGap; y + b <= h - wallGap + 1e-6; y += Step)
                    {
                        var (x1, y1) = (x + a, y + b);
                        (int, int)? front = null;
                        if (againstWall)
                        {
                            // Спиной к стене; лицевая сторона — в помещение, перед ней свободно board.
                            front = x <= 1e-6 && a == wid ? (1, 0) : x1 >= w - 1e-6 && a == wid ? (-1, 0) : y <= 1e-6 && b == wid ? (0, 1) : y1 >= h - 1e-6 && b == wid ? (0, -1) : null;
                            if (front is not { } f)
                            {
                                continue;
                            }

                            var (fx0, fy0, fx1, fy1) = (Math.Min(x, x + (f.Item1 * (a + board))), Math.Min(y, y + (f.Item2 * (b + board))), Math.Max(x1, x1 + (f.Item1 * board)), Math.Max(y1, y1 + (f.Item2 * board)));
                            if (fx0 < -1e-6 || fy0 < -1e-6 || fx1 > w + 1e-6 || fy1 > h + 1e-6 || placed.Any(p => Gap(p, fx0, fy0, fx1, fy1) <= 0))
                            {
                                continue;
                            }
                        }

                        if (!DoorFree(x, y, x1, y1) || placed.Any(p => Gap(p, x, y, x1, y1) < gapTo(p.Kind) - 1e-6 || !FrontFree(p, x, y, x1, y1)))
                        {
                            continue;
                        }

                        var s = score(x, y, x1, y1);
                        if (s < bestScore)
                        {
                            (bestScore, best) = (s, new PlacedItem(kind, label, x, y, x1, y1, front));
                        }
                    }
                }
            }

            return best;
        }

        // Установка — в угол, дальний от двери.
        var far = door is { } dr ? (X: dr.X < w / 2 ? w : 0, Y: dr.Y < h / 2 ? h : 0) : (X: w, Y: h);
        double Dist(double x0, double y0, double x1, double y1, double px, double py) => Math.Sqrt(Math.Pow(Math.Clamp(px, x0, x1) - px, 2) + Math.Pow(Math.Clamp(py, y0, y1) - py, 2)) + Math.Sqrt(Math.Pow(((x0 + x1) / 2) - px, 2) + Math.Pow(((y0 + y1) / 2) - py, 2));
        foreach (var item in items.OrderBy(i => i.Kind))
        {
            for (var n = 0; n < item.Count; n++)
            {
                var label = item.Count > 1 ? $"{item.Label} {n + 1}" : item.Label;
                var anchor = placed.FirstOrDefault(p => p.Kind == StationItemKind.PumpSet);
                Func<double, double, double, double, double> score = anchor is null
                    ? (x0, y0, x1, y1) => Dist(x0, y0, x1, y1, far.X, far.Y)
                    : (x0, y0, x1, y1) => Dist(x0, y0, x1, y1, (anchor.X0 + anchor.X1) / 2, (anchor.Y0 + anchor.Y1) / 2);
                var p = item.Kind switch
                {
                    StationItemKind.PumpSet => Best(item.Kind, label, item.Size.LengthMm, item.Size.WidthMm, moduleWall, _ => other, score, false),
                    StationItemKind.Cabinet => Best(item.Kind, label, item.Size.LengthMm, item.Size.WidthMm, 0, _ => other, score, true),
                    StationItemKind.ControlUnit => Best(item.Kind, label, item.Size.LengthMm, item.Size.WidthMm, units, k => k == StationItemKind.ControlUnit ? units : other, score, false),
                    _ => Best(item.Kind, label, item.Size.LengthMm, item.Size.WidthMm, 0, _ => other, score, false),
                };
                if (p is null)
                {
                    issues.Add(new Issue("pumpRoom.tooSmall", IssueLevel.Error,
                        $"В помещении насосной {w / 1000:0.00} × {h / 1000:0.00} м не помещается «{label}» ({item.Size.LengthMm / 1000:0.00} × {item.Size.WidthMm / 1000:0.00} м) с проходами п. 6.10.16; нужно не менее {MinArea(items, moduleWall, other, board) :0.0} м² (оценка).",
                        room.Id, "6.10.16"));
                    continue;
                }

                placed.Add(p);
            }
        }

        // В координаты чертежа.
        var world = placed.Select(p => p with { X0 = p.X0 + ox, X1 = p.X1 + ox, Y0 = p.Y0 + oy, Y1 = p.Y1 + oy }).ToList();
        return new PumpRoomPlan(room, world, issues, rules);
    }

    // Оценка нужной площади: каждый предмет со своими проходами по половине с каждой стороны, установка — с проходами до стен.
    private static double MinArea(IReadOnlyList<StationItem> items, double moduleWall, double other, double board) =>
        items.Sum(i => i.Kind switch
        {
            StationItemKind.PumpSet => (i.Size.LengthMm + (2 * moduleWall)) * (i.Size.WidthMm + (2 * moduleWall)),
            StationItemKind.Cabinet => (i.Size.LengthMm + other) * (i.Size.WidthMm + board),
            _ => i.Count * (i.Size.LengthMm + other) * (i.Size.WidthMm + other),
        }) / 1e6;
}
