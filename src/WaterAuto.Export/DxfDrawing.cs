using System.Globalization;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;
using WaterAuto.Core;

namespace WaterAuto.Export;

/// <summary>Общее оформление DXF плана и аксонометрии: слои и подписи участков.</summary>
internal static class DxfDrawing
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>
    /// Запись по расширению: .dwg и .dwt (шаблон) — DWG (его вставляет команда AutoCAD INSERT и WA-INSERT), иначе DXF.
    /// </summary>
    public static void Save(string path, CadDocument doc, string font = DocumentStyle.DefaultFont)
    {
        ApplyTextStyle(doc, font);
        if (Path.GetExtension(path).ToLowerInvariant() is ".dwg" or ".dwt")
        {
            DwgWriter.Write(path, doc);
        }
        else
        {
            DxfWriter.Write(path, doc, false);
        }
    }

    /// <summary>Стиль текста для всех подписей программы; шрифт — шрифт проекта (по умолчанию GOST Common, TrueType).</summary>
    public const string TextStyleName = "WA_ГОСТ";

    /// <summary>Файл шрифта стиля: имя шрифта с расширением .ttf, если расширение (.ttf, .shx) не указано.</summary>
    public static string FontFile(string font) => Path.HasExtension(font) ? font : font + ".ttf";

    // Слои, созданные программой в документе: стиль надписей меняется только на них (чертёж-подложка не трогается).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CadDocument, HashSet<string>> OwnLayers = new();

    /// <summary>Чертёж-подложка по расширению: .dwg — DWG, иначе DXF.</summary>
    public static CadDocument Read(string path) =>
        Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase) ? DwgReader.Read(path) : DxfReader.Read(path);

    // Надписи программы — стилем WA_ГОСТ; если шрифта нет на компьютере, AutoCAD подставит замену.
    private static void ApplyTextStyle(CadDocument doc, string font)
    {
        var own = OwnLayers.TryGetValue(doc, out var set) ? set : [];
        if (!doc.TextStyles.TryGetValue(TextStyleName, out var style))
        {
            style = new TextStyle(TextStyleName) { Width = 1.0 };
            doc.TextStyles.Add(style);
        }

        style.Filename = FontFile(font);

        foreach (var e in doc.Entities.Where(e => own.Contains(e.Layer.Name)))
        {
            switch (e)
            {
                case TextEntity t:
                    t.Style = style;
                    break;
                case MText m:
                    m.Style = style;
                    break;
            }
        }
    }

    /// <summary>Слой программы; в чертеже-подложке уже существующий слой (например, из шаблона) используется с цветом и толщиной программы.</summary>
    public static Layer AddLayer(CadDocument doc, string name, short color, LineWeightType lineWeight = LineWeightType.Default)
    {
        OwnLayers.GetOrCreateValue(doc).Add(name);
        if (doc.Layers.TryGetValue(name, out var existing))
        {
            existing.Color = new Color(color);
            existing.LineWeight = lineWeight;
            return existing;
        }

        var layer = new Layer(name) { Color = new Color(color), LineWeight = lineWeight };
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
