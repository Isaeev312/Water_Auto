using System.Globalization;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Types.Units;
using CSMath;
using WaterAuto.Core;

namespace WaterAuto.Export;

/// <summary>
/// Оформление аксонометрии, мм. Проекция — фронтальная изометрическая без искажения по осям: X плана — горизонтально,
/// отметка — вертикально, Y плана — под углом <paramref name="YAxisAngleDeg"/>. Угол и направление оси Y сверить
/// с требованиями к листу (ГОСТ 21.601) — это параметр оформления.
/// </summary>
public sealed record AxonometryStyle(
    double YAxisAngleDeg = 45,
    double TextHeightMm = 200,
    double SprinklerRadiusMm = 100,
    double RiserLengthMm = 1000);

/// <summary>
/// Черновик аксонометрической схемы (лист ПТ-5) из трассы: трубы, оросители, подписи DN и длин
/// (на подводящем, питающем и где DN меняется), стояк от ввода вниз с отметкой трубопровода.
/// Отметка одна на всю трассу — разные уровни и стояки появятся с этапом 3.
/// </summary>
public static class AxonometryDxfWriter
{
    public const string PipeLayer = "ПТ_АКСОН_ТРУБЫ";
    public const string SprinklerLayer = "ПТ_АКСОН_ОРОСИТЕЛИ";
    public const string LabelLayer = "ПТ_АКСОН_ПОДПИСИ";

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static XY Project(PlanPoint p, double elevationMm, AxonometryStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        var a = style.YAxisAngleDeg * Math.PI / 180;
        return new XY(p.XMm + (p.YMm * Math.Cos(a)), elevationMm + (p.YMm * Math.Sin(a)));
    }

    public static void Write(string path, IReadOnlyList<PipeRoute> routes, Length pipeElevation, AxonometryStyle? style = null)
    {
        DxfDrawing.Save(path, Build(routes, pipeElevation, style ?? new AxonometryStyle()));
    }

    public static CadDocument Build(IReadOnlyList<PipeRoute> routes, Length pipeElevation, AxonometryStyle style)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(style);
        var doc = new CadDocument(ACadVersion.AC1032);
        doc.Header.InsUnits = UnitsType.Millimeters;
        var pipes = DxfDrawing.AddLayer(doc, PipeLayer, 4, LineWeightType.W40); // трубопроводы — 0,40 мм
        var sprinklers = DxfDrawing.AddLayer(doc, SprinklerLayer, 1);
        var labels = DxfDrawing.AddLayer(doc, LabelLayer, 7);
        var z = pipeElevation.Millimeters;
        XYZ At(XY p) => new(p.X, p.Y, 0);

        foreach (var route in routes)
        {
            var nodes = route.Nodes.ToDictionary(n => n.Id);
            var byTo = route.Segments.ToDictionary(s => s.To);
            var loops = route.Loops.Select(s => s.Id).ToHashSet();
            foreach (var seg in route.Segments.Concat(route.Loops))
            {
                var a = Project(nodes[seg.From].Location, z, style);
                var b = Project(nodes[seg.To].Location, z, style);
                doc.Entities.Add(new Line(At(a), At(b)) { Layer = pipes });
                if (loops.Contains(seg.Id) || DxfDrawing.NeedsLabel(seg, byTo))
                {
                    doc.Entities.Add(DxfDrawing.Label(seg, a, b, style.TextHeightMm, labels));
                }
            }

            foreach (var n in route.Nodes.Where(n => n.Kind == RouteNodeKind.Sprinkler))
            {
                doc.Entities.Add(new Circle(At(Project(n.Location, z, style)), style.SprinklerRadiusMm) { Layer = sprinklers });
            }

            var inlet = Project(nodes[route.InletId].Location, z, style);
            doc.Entities.Add(new Line(At(inlet), new XYZ(inlet.X, inlet.Y - style.RiserLengthMm, 0)) { Layer = pipes });
            doc.Entities.Add(new TextEntity
            {
                Value = $"Ввод {route.InletId}, отм. {Elevation(pipeElevation)}",
                InsertPoint = new XYZ(inlet.X + style.TextHeightMm, inlet.Y - style.RiserLengthMm, 0),
                Height = style.TextHeightMm,
                Layer = labels,
            });
        }

        return doc;
    }

    // Отметка в метрах со знаком, как на чертежах: ±0,000, +3,300, −1,500.
    private static string Elevation(Length z) =>
        (z.Meters < 0 ? "−" : z.Meters > 0 ? "+" : "±") + Math.Abs(z.Meters).ToString("0.000", Ru);
}
