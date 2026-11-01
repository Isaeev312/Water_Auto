using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace WaterAuto.Export;

/// <summary>Удостоверяемый электронный документ: графы 2–9 формы 15.</summary>
public sealed record IulDocument(string Designation, string Name, string Version, string Algorithm, string Checksum, string FileName, DateTime Modified, long Size);

/// <summary>Строка подписей: характер работы (графа 10) и фамилия (графа 11); подпись и дата — от руки или ЭП.</summary>
public sealed record IulSignature(string Role, string Name);

/// <summary>
/// Удостоверяющий лист по форме 15 ГОСТ Р 21.101-2026 (прил. У) на листе А4 и склейка PDF тома (ADR 0015).
/// </summary>
public static class IulPdf
{
    private const double Mm = 72 / 25.4;

    /// <summary>MD5 файла — шестнадцатеричной строкой в верхнем регистре (графа 6).</summary>
    /// <remarks>Контрольная сумма целостности, не защита: алгоритм — по согласованию с принимающей стороной (Х.6), MD5 — принятый в экспертизах.</remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351", Justification = "Контрольная сумма УЛ, не криптография.")]
    public static string Md5(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.MD5.HashData(stream));
    }

    /// <summary>Склеивает PDF по порядку в один файл; возвращает число страниц.</summary>
    public static int Merge(IEnumerable<string> sources, string target, string title)
    {
        ArgumentNullException.ThrowIfNull(sources);
        using var output = new PdfDocument();
        output.Info.Title = title;
        output.Info.Creator = "Water_Auto";
        foreach (var source in sources)
        {
            using var input = PdfReader.Open(source, PdfDocumentOpenMode.Import);
            foreach (var page in input.Pages)
            {
                output.AddPage(page);
            }
        }

        var count = output.PageCount;
        output.Save(target);
        return count;
    }

    /// <summary>УЛ на один документ или комплект (тогда графа 1 — порядковые номера); обозначение УЛ — графа 14.</summary>
    public static void Write(string path, string designation, IReadOnlyList<IulDocument> documents, IReadOnlyList<IulSignature> signatures, string font)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(signatures);
        DxfPdf.EnsureFonts();
        var family = DxfPdf.FontAvailable(font) ? font : "Arial";
        using var pdf = new PdfDocument();
        pdf.Info.Title = designation;
        pdf.Info.Creator = "Water_Auto";
        var page = pdf.AddPage();
        page.Width = XUnit.FromMillimeter(210);
        page.Height = XUnit.FromMillimeter(297);
        using (var g = XGraphics.FromPdfPage(page))
        {
            var thick = new XPen(XColors.Black, 0.5 * Mm);
            var thin = new XPen(XColors.Black, 0.18 * Mm);
            var text = new XFont(family, 9);
            var small = new XFont(family, 7);
            var title = new XFont(family, 12);
            XRect R(double x, double y, double w, double h) => new(x * Mm, y * Mm, w * Mm, h * Mm);

            // Рамка: 20 мм слева, 5 мм с остальных сторон (ГОСТ Р 21.101, как у листов комплекта).
            const double x0 = 20, x1 = 205, w = x1 - x0;
            g.DrawRectangle(thick, R(x0, 5, w, 287));

            double y = 15;
            g.DrawString("Удостоверяющий лист", title, XBrushes.Black, R(x0, y, w, 8), XStringFormats.Center);
            y += 14;

            void Cell(double x, double yy, double cw, double ch, string value, XFont f, bool center = true)
            {
                g.DrawRectangle(thin, R(x, yy, cw, ch));
                var lines = Wrap(g, value, f, (cw - 2) * Mm);
                var lh = f.Size * 1.15;
                var top = (yy * Mm) + ((ch * Mm) - (lines.Count * lh)) / 2;
                for (var i = 0; i < lines.Count; i++)
                {
                    var rect = new XRect((x + 1) * Mm, top + (i * lh), (cw - 2) * Mm, lh);
                    g.DrawString(lines[i], f, XBrushes.Black, rect, center ? XStringFormats.Center : XStringFormats.CenterLeft);
                }
            }

            // Графы 1–4, 5–6, 7–9 — на каждый документ.
            double[] c1 = [15, 50, 90, 30];
            double[] c3 = [85, 50, 50];
            void Row(double[] widths, double yy, double h, string[] values, XFont f)
            {
                var x = x0;
                for (var i = 0; i < widths.Length; i++)
                {
                    Cell(x, yy, widths[i], h, values[i], f);
                    x += widths[i];
                }
            }

            for (var k = 0; k < documents.Count; k++)
            {
                var d = documents[k];
                Row(c1, y, 12, ["Номер п/п", "Обозначение документа", "Наименование документа", "Номер последнего изменения (версии)"], small);
                y += 12;
                Row(c1, y, 14, [documents.Count > 1 ? (k + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "", d.Designation, d.Name, d.Version], text);
                y += 14;
                Row([30, w - 30], y, 9, [d.Algorithm, d.Checksum], text);
                y += 9;
                Row(c3, y, 9, ["Наименование файла", "Дата и время последнего изменения файла", "Размер файла, байт"], small);
                y += 9;
                Row(c3, y, 12, [d.FileName, d.Modified.ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture), d.Size.ToString(System.Globalization.CultureInfo.InvariantCulture)], text);
                y += 16;
            }

            // Графы 10–13.
            double[] c4 = [50, 55, 40, 40];
            Row(c4, y, 9, ["Характер работы", "Фамилия", "Подпись", "Дата подписания"], small);
            y += 9;
            foreach (var s in signatures)
            {
                Row(c4, y, 9, [s.Role, s.Name, "", ""], text);
                y += 9;
            }

            // Графы 14–16 внизу листа; на одной странице номер и число страниц не заполняются.
            const double yb = 292 - 15;
            Cell(x0, yb, w - 30, 15, designation, title);
            Cell(x1 - 30, yb, 15, 6, "Лист", small);
            Cell(x1 - 15, yb, 15, 6, "Листов", small);
            Cell(x1 - 30, yb + 6, 15, 9, "", text);
            Cell(x1 - 15, yb + 6, 15, 9, "", text);
        }

        pdf.Save(path);
    }

    private static List<string> Wrap(XGraphics g, string value, XFont font, double width)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in value.Split(' '))
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

        // Длинное слово без пробелов (контрольная сумма, имя файла) — по знакам.
        return [.. lines.SelectMany(l => SplitLong(g, l, font, width))];
    }

    private static IEnumerable<string> SplitLong(XGraphics g, string line, XFont font, double width)
    {
        while (line.Length > 1 && g.MeasureString(line, font).Width > width)
        {
            var n = line.Length - 1;
            while (n > 1 && g.MeasureString(line[..n], font).Width > width)
            {
                n--;
            }

            yield return line[..n];
            line = line[n..];
        }

        yield return line;
    }
}
