using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using WaterAuto.Core;

namespace WaterAuto.Export;

/// <summary>Формат листа (ГОСТ 2.301), альбомная ориентация, мм.</summary>
public sealed record SheetFormat(string Name, double WidthMm, double HeightMm)
{
    public static readonly SheetFormat A3 = new("А3", 420, 297);
    public static readonly SheetFormat A2 = new("А2", 594, 420);
    public static readonly SheetFormat A1 = new("А1", 841, 594);
}

/// <summary>Лист в пространстве модели: формат, масштаб 1:<see cref="Scale"/> и положение левого нижнего угла листа в мм модели.</summary>
public sealed record SheetPlacement(SheetFormat Format, double Scale, XY Origin);

/// <summary>
/// Лист чертежа по ГОСТ Р 21.101 в пространстве модели DXF: рамка (20 мм слева, 5 мм с остальных сторон), основная надпись
/// по форме 3 (185×55) и дополнительные графы 20–22 на поле для подшивки. Размеры листа — в мм бумаги, умножаются на масштаб.
/// </summary>
public static class GostSheet
{
    public const string Layer = "ПТ_ФОРМАТ";

    // Масштабы ГОСТ 2.302 для схем и планов.
    private static readonly double[] Scales = [25, 50, 75, 100, 150, 200, 250, 300, 400, 500, 800, 1000];
    private static readonly SheetFormat[] Formats = [SheetFormat.A3, SheetFormat.A2, SheetFormat.A1];

    // Поле для изображения над основной надписью, мм бумаги: отступ от рамки 10 мм.
    private static (double X0, double Y0, double X1, double Y1) Field(SheetFormat f) => (30, 70, f.WidthMm - 15, f.HeightMm - 15);

    /// <summary>
    /// Подбор листа под изображение (габариты в мм модели): наименьший формат, на котором оно помещается в масштабе не мельче 1:200,
    /// и наиболее крупный такой масштаб; иначе — А1 в первом подходящем масштабе. Изображение — по центру поля над основной надписью.
    /// </summary>
    public static SheetPlacement Fit(double minX, double minY, double maxX, double maxY)
    {
        var w = Math.Max(maxX - minX, 1);
        var h = Math.Max(maxY - minY, 1);
        bool Fits(SheetFormat f, double s)
        {
            var (x0, y0, x1, y1) = Field(f);
            return w / s <= x1 - x0 && h / s <= y1 - y0;
        }

        var (format, scale) = Formats.SelectMany(f => Scales.Where(s => s <= 200).Select(s => (f, s))).FirstOrDefault(c => Fits(c.f, c.s));
        if (format is null)
        {
            format = SheetFormat.A1;
            scale = Scales.FirstOrDefault(s => Fits(format, s), Scales[^1]);
        }

        var (fx0, fy0, fx1, fy1) = Field(format);
        var origin = new XY(((minX + maxX) / 2) - (((fx0 + fx1) / 2) * scale), ((minY + maxY) / 2) - (((fy0 + fy1) / 2) * scale));
        return new SheetPlacement(format, scale, origin);
    }

    /// <summary>Рамка, основная надпись по форме 3 и дополнительные графы на слое <see cref="Layer"/>.</summary>
    public static void Draw(CadDocument doc, SheetPlacement sheet, TitleBlock block)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(block);
        var layer = DxfDrawing.AddLayer(doc, Layer, 7);
        var s = sheet.Scale;
        var w = sheet.Format.WidthMm;
        var h = sheet.Format.HeightMm;
        XYZ P(double x, double y) => new(sheet.Origin.X + (x * s), sheet.Origin.Y + (y * s), 0);

        void Seg(double x0, double y0, double x1, double y1, bool thick = true) =>
            doc.Entities.Add(new Line(P(x0, y0), P(x1, y1)) { Layer = layer, LineWeight = thick ? LineWeightType.W50 : LineWeightType.W18 });

        void Rect(double x0, double y0, double x1, double y1)
        {
            Seg(x0, y0, x1, y0);
            Seg(x1, y0, x1, y1);
            Seg(x1, y1, x0, y1);
            Seg(x0, y1, x0, y0);
        }

        // Текст по центру ячейки (x0..x1, y0..y1), высота — в мм бумаги; длинный переносится по ширине ячейки, короткие подписи граф — без переноса.
        void Text(string value, double x0, double y0, double x1, double y1, double height = 3.5, bool left = false, bool vertical = false, bool wrap = true)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            doc.Entities.Add(new MText
            {
                Value = value,
                InsertPoint = left ? P(x0 + 1, (y0 + y1) / 2) : P((x0 + x1) / 2, (y0 + y1) / 2),
                Height = height * s,
                RectangleWidth = wrap ? (vertical ? y1 - y0 : x1 - x0 - 1) * s : 0,
                AttachmentPoint = left ? AttachmentPointType.MiddleLeft : AttachmentPointType.MiddleCenter,
                AlignmentPoint = vertical ? XYZ.AxisY : XYZ.AxisX, // направление текста
                Layer = layer,
            });
        }

        // Внешняя граница листа — тонкой линией (линия обреза), рамка — основной.
        Seg(0, 0, w, 0, false);
        Seg(w, 0, w, h, false);
        Seg(w, h, 0, h, false);
        Seg(0, h, 0, 0, false);
        Rect(20, 5, w - 5, h - 5);
        Text($"Формат {sheet.Format.Name}", w - 40, 0.5, w - 5, 4.5, 2.5);

        // Основная надпись (форма 3): левая часть 65 мм — 11 строк по 5 мм, правая 120 мм — графы 1–9.
        var x = w - 5 - 185;
        const double y = 5;
        double Row(int fromTop) => y + 55 - (5 * fromTop);
        Rect(x, y, x + 185, y + 55);
        for (var r = 1; r < 11; r++)
        {
            // Шапка «Изм. Кол.уч. …» (строка 4) — основными линиями, остальные строки — тонкими.
            Seg(x, Row(r), x + 65, Row(r), r is 3 or 4);
        }

        Seg(x + 10, Row(4), x + 10, Row(0));
        Seg(x + 20, y, x + 20, Row(0));
        Seg(x + 30, Row(4), x + 30, Row(0));
        Seg(x + 40, y, x + 40, Row(0));
        Seg(x + 55, y, x + 55, Row(0));
        Seg(x + 65, y, x + 65, Row(0));
        Seg(x + 65, Row(2), x + 185, Row(2));
        Seg(x + 65, Row(5), x + 185, Row(5));
        Seg(x + 65, Row(8), x + 185, Row(8));
        Seg(x + 135, y, x + 135, Row(5));
        Seg(x + 135, Row(6), x + 185, Row(6));
        Seg(x + 150, Row(8), x + 150, Row(5));
        Seg(x + 165, Row(8), x + 165, Row(5));

        string[] head = ["Изм.", "Кол.уч.", "Лист", "№ док.", "Подп.", "Дата"];
        double[] cols = [0, 10, 20, 30, 40, 55, 65];
        for (var c = 0; c < head.Length; c++)
        {
            Text(head[c], x + cols[c], Row(4), x + cols[c + 1], Row(3), 2.5, wrap: false);
        }

        string[] bottom = ["Н. контр.", "Утв."];
        var top = block.Signatures.Where(g => !bottom.Contains(g.Role)).ToList();
        var tail = block.Signatures.Where(g => bottom.Contains(g.Role)).ToList();
        var rows = new TitleSignature?[7];
        for (var k = 0; k < top.Count && k < rows.Length - tail.Count; k++)
        {
            rows[k] = top[k];
        }

        for (var k = 0; k < tail.Count && k < rows.Length; k++)
        {
            rows[rows.Length - tail.Count + k] = tail[k];
        }

        for (var k = 0; k < rows.Length; k++)
        {
            if (rows[k] is { } g)
            {
                Text(g.Role, x, Row(5 + k), x + 20, Row(4 + k), 2.5, left: true);
                Text(g.Name, x + 20, Row(5 + k), x + 40, Row(4 + k), 2.5, left: true);
            }
        }

        Text(block.Designation, x + 65, Row(2), x + 185, Row(0), 5);
        Text(block.Complex, x + 65, Row(5), x + 185, Row(2));
        Text(block.Building, x + 65, Row(8), x + 135, Row(5));
        Text("Стадия", x + 135, Row(6), x + 150, Row(5), 2.5);
        Text("Лист", x + 150, Row(6), x + 165, Row(5), 2.5);
        Text("Листов", x + 165, Row(6), x + 185, Row(5), 2.5);
        Text(block.Stage, x + 135, Row(8), x + 150, Row(6));
        Text(block.Sheet ?? "", x + 150, Row(8), x + 165, Row(6));
        Text(block.SheetCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", x + 165, Row(8), x + 185, Row(6));
        Text(block.Name, x + 65, y, x + 135, Row(8));
        Text(block.Organization, x + 135, y, x + 185, Row(8));

        // Дополнительные графы 20–22 на поле для подшивки: снизу вверх 25, 35, 25 мм.
        Rect(8, 5, 20, 90);
        Seg(13, 5, 13, 90);
        Seg(8, 30, 20, 30);
        Seg(8, 65, 20, 65);
        Text("Инв. № подл.", 8, 5, 13, 30, 2.5, vertical: true);
        Text("Подп. и дата", 8, 30, 13, 65, 2.5, vertical: true);
        Text("Взам. инв. №", 8, 65, 13, 90, 2.5, vertical: true);
    }
}
