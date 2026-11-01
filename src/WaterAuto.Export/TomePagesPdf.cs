using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace WaterAuto.Export;

/// <summary>Обложка и титульный лист тома проектной документации (ГОСТ Р 21.101-2020, разд. 5).</summary>
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
    public static void WriteContents(string path, string designation, string stage, string organization, IReadOnlyList<IulSignature> signatures, IReadOnlyList<TomeContentRow> rows, string font)
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

                // Основная надпись для текстовых документов (форма 5, упрощённо): графы подписей, обозначение, наименование, стадия, лист, листов.
                const double sy = 292 - 40;
                const double sx = 20;
                g.DrawRectangle(thick, R(sx, sy, 185, 40));
                g.DrawString(designation + ".С", new XFont(family, 12), XBrushes.Black, R(sx + 65, sy, 120, 10), XStringFormats.Center);
                g.DrawString("Содержание тома", text, XBrushes.Black, R(sx + 65, sy + 15, 70, 25), XStringFormats.Center);
                g.DrawString(organization, text, XBrushes.Black, R(sx + 135, sy + 25, 50, 15), XStringFormats.Center);
                string[] cells = ["Стадия", "Лист", "Листов"];
                string[] values = [stage, "1", "1"];
                for (var i = 0; i < 3; i++)
                {
                    g.DrawRectangle(thin, R(sx + 135 + (i * 50.0 / 3), sy + 15, 50.0 / 3, 5));
                    g.DrawRectangle(thin, R(sx + 135 + (i * 50.0 / 3), sy + 20, 50.0 / 3, 5));
                    g.DrawString(cells[i], small, XBrushes.Black, R(sx + 135 + (i * 50.0 / 3), sy + 15, 50.0 / 3, 5), XStringFormats.Center);
                    g.DrawString(values[i], small, XBrushes.Black, R(sx + 135 + (i * 50.0 / 3), sy + 20, 50.0 / 3, 5), XStringFormats.Center);
                }

                g.DrawLine(thin, (sx + 65) * Mm, sy * Mm, (sx + 65) * Mm, (sy + 40) * Mm);
                g.DrawLine(thin, (sx + 65) * Mm, (sy + 10) * Mm, (sx + 185) * Mm, (sy + 10) * Mm);
                g.DrawLine(thin, (sx + 65) * Mm, (sy + 15) * Mm, (sx + 185) * Mm, (sy + 15) * Mm);
                g.DrawLine(thin, (sx + 135) * Mm, (sy + 15) * Mm, (sx + 135) * Mm, (sy + 40) * Mm);
                var yy = sy + 15;
                foreach (var s in signatures.Take(5))
                {
                    g.DrawLine(thin, sx * Mm, yy * Mm, (sx + 65) * Mm, yy * Mm);
                    g.DrawString(s.Role, small, XBrushes.Black, R(sx + 1, yy, 18, 5), XStringFormats.CenterLeft);
                    g.DrawString(s.Name, small, XBrushes.Black, R(sx + 20, yy, 25, 5), XStringFormats.CenterLeft);
                    yy += 5;
                }
            }

            pdf.Save(path);
        }
    }

    /// <summary>Страницы документа в томе: «5» или «5–12».</summary>
    public static string Pages(int first, int count) =>
        count <= 1 ? first.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{first}–{first + count - 1}");
}
