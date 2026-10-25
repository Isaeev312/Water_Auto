using System.Globalization;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using WaterAuto.Core;

namespace WaterAuto.Export;

/// <summary>Общее оформление DXF плана и аксонометрии: слои и подписи участков.</summary>
internal static class DxfDrawing
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static Layer AddLayer(CadDocument doc, string name, short color)
    {
        var layer = new Layer(name) { Color = new Color(color) };
        doc.Layers.Add(layer);
        return layer;
    }

    /// <summary>Подписывается подводящий и питающий, а на рядках — только участок, где DN меняется.</summary>
    public static bool NeedsLabel(RouteSegment seg, IReadOnlyDictionary<string, RouteSegment> byTo) =>
        seg.Role != RouteSegmentRole.Row || !byTo.TryGetValue(seg.From, out var parent) || parent.Dn != seg.Dn;

    /// <summary>Подпись участка «DN32 L=1,50 м» над серединой, вдоль трубы, не вверх ногами.</summary>
    public static TextEntity Label(RouteSegment seg, XY a, XY b, double textHeight, Layer layer)
    {
        var angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
        if (angle > Math.PI / 2 || angle <= -Math.PI / 2)
        {
            angle += Math.PI;
        }

        var mid = new XYZ(((a.X + b.X) / 2) - (Math.Sin(angle) * textHeight * 0.5), ((a.Y + b.Y) / 2) + (Math.Cos(angle) * textHeight * 0.5), 0);
        return new TextEntity
        {
            Value = string.Create(Ru, $"DN{seg.Dn} L={seg.Length.Meters:0.00} м"),
            InsertPoint = mid,
            Height = textHeight,
            Rotation = angle,
            Layer = layer,
        };
    }
}
