using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace WaterAuto.Export;

/// <summary>Обложка и титульный лист тома проектной документации (ГОСТ Р 21.101-2026, разд. 5).</summary>
/// <param name="Signatures">Подписи титульного листа (должность, фамилия); на обложке не ставятся.</param>
public sealed record TomeCover(
    string Organization,
    string Customer,
    string ObjectName,
    string DocumentKind,
    string SectionTitle,
    string Designation,
    string Volume,
    string Year,
    IReadOnlyList<IulSignature> Signatures);

/// <summary>Строка содержания тома: обозначение, наименование, страницы («3–32»).</summary>
public sealed record TomeContentRow(string Designation, string Name, string Pages);

/// <summary>Служебные страницы тома: обложка, титульный лист, содержание тома (А4, ADR 0015).</summary>
public static class TomePagesPdf
{
    private const double Mm = 72 / 25.4;

    private static (PdfDocument Pdf, XGraphics G, string Family) A4(string font, string title)
    {
        DxfPdf.EnsureFonts();
        var family = DxfPdf.FontAvailable(font) ? font : "Arial";
        var pdf = new PdfDocument();
        pdf.Info.Title = title;
        pdf.Info.Creator = "Water_Auto";
        var page = pdf.AddPage();
        page.Width = XUnit.FromMillimeter(210);
        page.Height = XUnit.FromMillimeter(297);
        return (pdf, XGraphics.FromPdfPage(page), family);
    }

    private static XRect R(double x, double y, double w, double h) => new(x * Mm, y * Mm, w * Mm, h * Mm);

    // Текст по центру полосы шириной w с переносом по словам; возвращает y под последней строкой, мм.
    private static double Center(XGraphics g, string text, XFont font, double x, double y, double w)
    {
        var lineMm = font.Size * 1.3 / Mm;
        foreach (var line in Lines(g, text, font, w * Mm))
        {
            g.DrawString(line, font, XBrushes.Black, R(x, y, w, lineMm), XStringFormats.Center);
            y += lineMm;
        }

        return y;
    }

    private static List<string> Lines(XGraphics g, string text, XFont font, double width)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && g.MeasureString(candidate, font).Width > width)
                {
                    lines.Add(line);
                    line = word;
                }
                else
                {
                    line = candidate;
                }
            }

            lines.Add(line);
        }

        return lines;
    }

    /// <summary>Обложка (без подписей) или титульный лист (с подписями слева и справа над наименованием).</summary>
    public static void WriteCover(string path, TomeCover cover, bool titlePage, string font)
    {
        ArgumentNullException.ThrowIfNull(cover);
        var (pdf, g, family) = A4(font, cover.Designation);
        using (pdf)
        {
            using (g)
            {
                var normal = new XFont(family, 12);
                var big = new XFont(family, 16);
                var bold = new XFont(family, 14, XFontStyleEx.Bold);
                const double x = 25, w = 175;
                g.DrawRectangle(new XPen(XColors.Black, 0.5 * Mm), R(20, 5, 185, 287));
                var y = Center(g, cover.Organization, normal, x, 15, w);
                y = Center(g, "Заказчик: " + cover.Customer, normal, x, y + 6, w);

                if (titlePage && cover.Signatures.Count > 0)
                {
                    // Подписи титульного листа — колонкой справа: должность, линия подписи, фамилия.
                    var ys = y + 8;
                    foreach (var s in cover.Signatures)
                    {
                        g.DrawString(s.Role, normal, XBrushes.Black, R(110, ys, 90, 6), XStringFormats.CenterLeft);
                        g.DrawString("_______________ " + s.Name, normal, XBrushes.Black, R(110, ys + 7, 90, 6), XStringFormats.CenterLeft);
                        ys += 18;
                    }

                    y = ys;
                }

                y = Math.Max(y + 15, 95);
                y = Center(g, cover.ObjectName, big, x, y, w);
                y = Center(g, cover.DocumentKind, bold, x, y + 15, w);
                y = Center(g, cover.SectionTitle, normal, x, y + 8, w);
                y = Center(g, cover.Designation, bold, x, y + 10, w);
                Center(g, "Том " + cover.Volume, big, x, y + 6, w);
                Center(g, cover.Year, normal, x, 270, w);
            }

            pdf.Save(path);
        }
    }

    /// <summary>Содержание тома по форме ГОСТ Р 21.101 (обозначение, наименование, примечание — страницы) с основной надписью для текстовых документов.</summary>
    public static void WriteContents(string path, string designation, string stage, string organization, IReadOnlyList<IulSignature> signatures, IReadOnlyList<TomeContentRow> rows, string font, string? customer = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(signatures);
        var (pdf, g, family) = A4(font, designation);
        using (pdf)
        {
            using (g)
            {
                var thick = new XPen(XColors.Black, 0.5 * Mm);
                var thin = new XPen(XColors.Black, 0.18 * Mm);
                var text = new XFont(family, 10);
                var small = new XFont(family, 7);
                const double x0 = 20, x1 = 205;
                g.DrawRectangle(thick, R(x0, 5, x1 - x0, 287));
                g.DrawString("Содержание тома", new XFont(family, 13), XBrushes.Black, R(x0, 12, x1 - x0, 8), XStringFormats.Center);

                double[] widths = [60, 95, 30];
                string[] head = ["Обозначение", "Наименование", "Примечание"];
                var y = 24.0;
                void Row(string[] values, XFont f, double h)
                {
                    var x = x0;
                    for (var i = 0; i < widths.Length; i++)
                    {
                        g.DrawRectangle(thin, R(x, y, widths[i], h));
                        var lines = Lines(g, values[i], f, (widths[i] - 3) * Mm);
                        var lh = f.Size * 1.2;
                        for (var k = 0; k < lines.Count; k++)
                        {
                            g.DrawString(lines[k], f, XBrushes.Black, new XRect((x + 1.5) * Mm, (y * Mm) + 2 + (k * lh), (widths[i] - 3) * Mm, lh), i == 1 ? XStringFormats.TopLeft : XStringFormats.TopCenter);
                        }

                        x += widths[i];
                    }

                    y += h;
                }

                Row(head, text, 8);
                foreach (var r in rows)
                {
                    var n = Math.Max(Lines(g, r.Name, text, (widths[1] - 3) * Mm).Count, Lines(g, r.Designation, text, (widths[0] - 3) * Mm).Count);
                    Row([r.Designation, r.Name, r.Pages], text, Math.Max(8, (n * 4.3) + 2));
                }

                Form5(g, family, designation + ".С", "Содержание тома", stage, "1", "1", organization, signatures, customer);
            }

            pdf.Save(path);
        }
    }

    /// <summary>
    /// Основная надпись для текстовых документов, форма 5 ГОСТ Р 21.101-2026 (185 × 40 мм), и дополнительные графы 20–22 на поле
    /// для подшивки — те же графы, что в docx выпуска (GostFrame).
    /// </summary>
    public static void Form5(XGraphics g, string family, string designation, string name, string stage, string sheet, string sheets, string organization, IReadOnlyList<IulSignature> signatures, string? customer = null)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(signatures);
        var thick = new XPen(XColors.Black, 0.5 * Mm);
        var thin = new XPen(XColors.Black, 0.18 * Mm);
        var f7 = new XFont(family, 7);
        var f10 = new XFont(family, 10);
        var f12 = new XFont(family, 12);
        double[] cols = [10, 10, 10, 10, 15, 10, 70, 15, 15, 20];
        var cx = new double[cols.Length + 1];
        cx[0] = 20;
        for (var i = 0; i < cols.Length; i++)
        {
            cx[i + 1] = cx[i] + cols[i];
        }

        const double y0 = 292 - 40;
        double Ry(int row) => y0 + (row * 5);
        void Box(int row, int col, int rows, int span, string text, XFont font, bool left = false)
        {
            var rect = R(cx[col], Ry(row), cx[col + span] - cx[col], rows * 5);
            g.DrawRectangle(thin, rect);
            if (text.Length > 0)
            {
                var lines = Lines(g, text, font, rect.Width - (2 * Mm));
                var lh = font.Size * 1.15;
                var top = rect.Y + ((rect.Height - (lines.Count * lh)) / 2);
                for (var k = 0; k < lines.Count; k++)
                {
                    g.DrawString(lines[k], font, XBrushes.Black, new XRect(rect.X + Mm, top + (k * lh), rect.Width - (2 * Mm), lh), left ? XStringFormats.CenterLeft : XStringFormats.Center);
                }
            }
        }

        // Строка изменений, шапка, шесть строк подписей («Н. контр.» — внизу).
        for (var c = 0; c < 6; c++)
        {
            Box(0, c, 1, 1, "", f7);
        }

        string[] head = ["Изм.", "Кол.уч.", "Лист", "№ док.", "Подп.", "Дата"];
        for (var c = 0; c < 6; c++)
        {
            Box(1, c, 1, 1, head[c], f7);
        }

        var rows = new IulSignature?[6];
        var top6 = signatures.Where(x => x.Role != "Н. контр.").Take(5).ToList();
        for (var k = 0; k < top6.Count; k++)
        {
            rows[k] = top6[k];
        }

        rows[5] = signatures.FirstOrDefault(x => x.Role == "Н. контр.") ?? new IulSignature("Н. контр.", "");
        for (var k = 0; k < 6; k++)
        {
            Box(2 + k, 0, 1, 2, rows[k]?.Role ?? "", f7, left: true);
            Box(2 + k, 2, 1, 2, rows[k]?.Name ?? "", f7, left: true);
            Box(2 + k, 4, 1, 1, "", f7);
            Box(2 + k, 5, 1, 1, "", f7);
        }

        Box(0, 6, 3, 4, designation, f12);
        Box(3, 6, 5, 1, name, f10);
        Box(3, 7, 1, 1, "Вид док.", f7); // ГОСТ Р 21.101-2026, форма 5: графа 6 — «Вид док.»
        Box(3, 8, 1, 1, "Лист", f7);
        Box(3, 9, 1, 1, "Листов", f7);
        Box(4, 7, 1, 1, stage, f10);
        Box(4, 8, 1, 1, sheet, f10);
        Box(4, 9, 1, 1, sheets, f10);
        Box(5, 7, 3, 3, organization, f10);

        // Графа 27 (ГОСТ Р 21.101-2026) — заказчик, строка 10 мм над правой частью надписи.
        if (!string.IsNullOrWhiteSpace(customer) && !customer.TrimStart().StartsWith('['))
        {
            var rect = R(cx[6], y0 - 10, 120, 10);
            g.DrawRectangle(thin, rect);
            g.DrawString(customer, f10, XBrushes.Black, rect, XStringFormats.Center);
        }

        // Контур надписи и шапка — основными линиями.
        g.DrawRectangle(thick, R(20, y0, 185, 40));
        g.DrawLine(thick, cx[0] * Mm, Ry(1) * Mm, cx[6] * Mm, Ry(1) * Mm);
        g.DrawLine(thick, cx[0] * Mm, Ry(2) * Mm, cx[6] * Mm, Ry(2) * Mm);
        g.DrawLine(thick, cx[6] * Mm, y0 * Mm, cx[6] * Mm, (y0 + 40) * Mm);
        g.DrawLine(thick, cx[6] * Mm, Ry(3) * Mm, cx[10] * Mm, Ry(3) * Mm);
        g.DrawLine(thick, cx[7] * Mm, Ry(3) * Mm, cx[7] * Mm, (y0 + 40) * Mm);
        g.DrawLine(thick, cx[7] * Mm, Ry(5) * Mm, cx[10] * Mm, Ry(5) * Mm);

        // Графы 20–22 слева на поле для подшивки (снизу вверх 25, 35, 25 мм).
        string[] side = ["Инв. № подл.", "Подп. и дата", "Взам. инв. №"];
        double[] heights = [25, 35, 25];
        var yb = 292.0;
        for (var k = 0; k < 3; k++)
        {
            var yt = yb - heights[k];
            g.DrawRectangle(thick, R(8, yt, 12, heights[k]));
            g.DrawLine(thin, 13 * Mm, yt * Mm, 13 * Mm, yb * Mm);
            var state = g.Save();
            g.TranslateTransform(10.5 * Mm, (yt + (heights[k] / 2)) * Mm);
            g.RotateTransform(-90);
            g.DrawString(side[k], f7, XBrushes.Black, new XRect(-heights[k] / 2 * Mm, -2.5 * Mm, heights[k] * Mm, 5 * Mm), XStringFormats.Center);
            g.Restore(state);
            yb = yt;
        }
    }

    /// <summary>Страницы документа в томе: «5» или «5–12».</summary>
    public static string Pages(int first, int count) =>
        count <= 1 ? first.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{first}–{first + count - 1}");
}
