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
    /// <summary>Тип линии осей — штрихпунктирная тонкая (ГОСТ 2.303): штрих, промежуток, точка, промежуток; длины — в мм бумаги.</summary>
    public const string AxisLineTypeName = "WA_ОСЕВАЯ";

    /// <summary>Тип линии осей в документе (создаётся при первом обращении); масштаб к листу — LineTypeScale примитива.</summary>
    public static LineType AxisLineType(CadDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.LineTypes.TryGetValue(AxisLineTypeName, out var existing))
        {
            return existing;
        }

        // ГОСТ 2.303: штрих 5–30 мм, промежуток 3–5 мм — принято 12 и 3 мм.
        var type = new LineType(AxisLineTypeName) { Description = "Штрихпунктирная тонкая (ГОСТ 2.303) __ . __ ." };
        foreach (var length in new[] { 12.0, -3, 0, -3 })
        {
            type.AddSegment(new LineType.Segment { Length = length });
        }

        doc.LineTypes.Add(type);
        return type;
    }

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

    /// <summary>Подпись участка (по умолчанию «DN32 L=1,50 м») над серединой, вдоль трубы, не вверх ногами.</summary>
    public static TextEntity Label(RouteSegment seg, XY a, XY b, double textHeight, Layer layer, string? text = null)
    {
        var angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
        if (angle > Math.PI / 2 || angle <= -Math.PI / 2)
        {
            angle += Math.PI;
        }

        var mid = new XYZ(((a.X + b.X) / 2) - (Math.Sin(angle) * textHeight * 0.5), ((a.Y + b.Y) / 2) + (Math.Cos(angle) * textHeight * 0.5), 0);
        return new TextEntity
        {
            Value = text ?? string.Create(Ru, $"DN{seg.Dn} L={seg.Length.Meters:0.00} м"),
            InsertPoint = mid,
            Height = textHeight,
            Rotation = angle,
            Layer = layer,
        };
    }
}
