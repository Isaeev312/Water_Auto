using System.Text;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace WaterAuto.Export;

/// <summary>
/// Печать листа DXF в векторный PDF (ADR 0015): лист — по рамке на слое <see cref="GostSheet.Layer"/>, формат и масштаб —
/// по её размеру; линии и текст — чёрным, толщины — по весам линий DXF. Поддерживаются примитивы, которые пишет программа.
/// </summary>
public static partial class DxfPdf
{
    private const double PtPerMm = 72 / 25.4;

    // Вес линии «по умолчанию» в DXF — 0,25 мм.
    private const double DefaultWeightMm = 0.25;

    /// <summary>Печатает DXF в PDF; сообщения (пропущенные примитивы, замена шрифта) — в <paramref name="log"/>. false — в чертеже нет рамки листа.</summary>
    public static bool Render(string dxfPath, string pdfPath, string font, Action<string> log, bool color = true)
    {
        ArgumentNullException.ThrowIfNull(log);
        var doc = DxfDrawing.Read(dxfPath);
        using var stream = File.Create(pdfPath);
        var ok = Render(doc, stream, font, log, Path.GetFileNameWithoutExtension(dxfPath), color);
        if (!ok)
        {
            stream.Close();
            File.Delete(pdfPath);
        }

        return ok;
    }

    public static bool Render(CadDocument doc, Stream output, string font, Action<string> log, string title = "", bool color = true)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(log);
        var frame = doc.Entities.Where(e => e.Layer?.Name == GostSheet.Layer).ToList();
        if (frame.Count == 0)
        {
            log($"{title}: нет рамки листа (слой {GostSheet.Layer}) — PDF не выпущен.");
            return false;
        }

        var box = Bounds(frame);
        // У А3, А2, А1 одно соотношение сторон: формат — тот, при котором масштаб листа целый (листы программы — 1:1, 1:50, 1:100…).
        var format = new[] { SheetFormat.A3, SheetFormat.A2, SheetFormat.A1 }
            .MinBy(f => Math.Abs((box.Width / f.WidthMm) - Math.Max(1, Math.Round(box.Width / f.WidthMm))))!;
        var scale = box.Width / format.WidthMm;

        EnsureFonts();
        var family = FontAvailable(font) ? font : "Arial";
        if (family != font)
        {
            log($"{title}: шрифт «{font}» не установлен — текст напечатан шрифтом Arial.");
        }

        using var pdf = new PdfDocument();
        pdf.Info.Title = title;
        pdf.Info.Creator = "Water_Auto";
        var page = pdf.AddPage();
        page.Width = XUnit.FromMillimeter(format.WidthMm);
        page.Height = XUnit.FromMillimeter(format.HeightMm);
        using (var g = XGraphics.FromPdfPage(page))
        {
            var painter = new Painter(g, box.MinX, box.MinY, scale, format.HeightMm, family, color, doc.Header.LineTypeScale > 0 ? doc.Header.LineTypeScale : 1);
            foreach (var e in doc.Entities)
            {
                painter.Draw(e, Affine.Identity);
            }

            foreach (var skipped in painter.Skipped.Order())
            {
                log($"{title}: примитив {skipped} не печатается.");
            }
        }

        pdf.Save(output);
        return true;
    }

    private static readonly object FontLock = new();

    internal static void EnsureFonts()
    {
        lock (FontLock)
        {
            if (GlobalFontSettings.FontResolver is null)
            {
                if (OperatingSystem.IsWindows())
                {
                    GlobalFontSettings.UseWindowsFontsUnderWindows = true;
                }

                GlobalFontSettings.FontResolver = new FileFontResolver();
            }
        }
    }

    /// <summary>
    /// Шрифт по имени файла: семейство «GOST Common» — файл «GOST Common.ttf» в папке шрифтов Windows или пользователя
    /// (так шрифт задан и в стиле текста DXF). Не найден — стандартный поиск PDFsharp.
    /// </summary>
    private sealed class FileFontResolver : IFontResolver
    {
        private static readonly string[] Folders =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"),
        ];

        private static string? File(string family) => Folders
            .Where(f => f.Length > 0)
            .Select(f => Path.Combine(f, family + ".ttf"))
            .FirstOrDefault(System.IO.File.Exists);

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
            File(familyName) is { } path ? new FontResolverInfo(path) : PlatformFontResolver.ResolveTypeface(familyName, bold, italic);

        public byte[]? GetFont(string faceName) => System.IO.File.Exists(faceName) ? System.IO.File.ReadAllBytes(faceName) : null;
    }

    internal static bool FontAvailable(string family)
    {
        try
        {
            var f = new XFont(family, 10);
            return string.Equals(f.FontFamily.Name, family, StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Рисунок штрихов типа линии в мм бумаги: длины сегментов × масштаб документа × масштаб примитива / масштаб листа
    /// (штрих, промежуток, точка = 0); сплошная — пусто.
    /// </summary>
    public static double[] DashPatternMm(LineType type, double headerScale, double entityScale, double sheetScale)
    {
        ArgumentNullException.ThrowIfNull(type);
        var k = headerScale * entityScale / sheetScale;
        return [.. type.Segments.Select(seg => Math.Abs(seg.Length) * k)];
    }

    private readonly record struct Box(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double Width => MaxX - MinX;

        public double Height => MaxY - MinY;
    }

    private static Box Bounds(IEnumerable<Entity> entities)
    {
        var (x0, y0, x1, y1) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
        foreach (var e in entities)
        {
            var b = e.GetBoundingBox();
            x0 = Math.Min(x0, b.Min.X);
            y0 = Math.Min(y0, b.Min.Y);
            x1 = Math.Max(x1, b.Max.X);
            y1 = Math.Max(y1, b.Max.Y);
        }

        return new Box(x0, y0, x1, y1);
    }

    /// <summary>Аффинное преобразование плоскости (вставки блоков).</summary>
    private readonly record struct Affine(double A, double B, double C, double D, double E, double F)
    {
        public static Affine Identity => new(1, 0, 0, 1, 0, 0);

        public (double X, double Y) Apply(double x, double y) => ((A * x) + (C * y) + E, (B * x) + (D * y) + F);

        // this ∘ inner: сначала inner, затем this.
        public Affine Then(Affine outer) => new(
            (outer.A * A) + (outer.C * B),
            (outer.B * A) + (outer.D * B),
            (outer.A * C) + (outer.C * D),
            (outer.B * C) + (outer.D * D),
            (outer.A * E) + (outer.C * F) + outer.E,
            (outer.B * E) + (outer.D * F) + outer.F);

        public double ScaleFactor => Math.Sqrt(Math.Abs((A * D) - (B * C)));

        public double Angle => Math.Atan2(B, A);
    }

    private sealed class Painter(XGraphics g, double minX, double minY, double scale, double pageHeightMm, string family, bool color, double ltScale)
    {
        // Цвет текущего примитива: свой, слоя или вставки блока (ПоБлоку); белый (7) — чёрным, светлые — темнее для печати на белом.
        private XColor ink = XColors.Black;

        private XColor Ink(Entity e, XColor parent)
        {
            if (!color)
            {
                return XColors.Black;
            }

            var c = e.Color;
            if (c.IsByBlock)
            {
                return parent;
            }

            if (c.IsByLayer)
            {
                c = e.Layer?.Color ?? new Color(7);
            }

            var (r, gg, b) = ((double)c.R, (double)c.G, (double)c.B);
            if (r > 250 && gg > 250 && b > 250)
            {
                return XColors.Black;
            }

            var luma = (0.299 * r) + (0.587 * gg) + (0.114 * b);
            var k = luma > 140 ? 140 / luma : 1;
            return XColor.FromArgb((int)(r * k), (int)(gg * k), (int)(b * k));
        }

        public HashSet<string> Skipped { get; } = [];

        private XPoint P(Affine t, double x, double y)
        {
            var (mx, my) = t.Apply(x, y);
            return new XPoint((mx - minX) / scale * PtPerMm, (pageHeightMm - ((my - minY) / scale)) * PtPerMm);
        }

        private static double WeightMm(Entity e, Layer? layer)
        {
            var w = e.LineWeight;
            if (w is LineWeightType.ByLayer or LineWeightType.ByBlock)
            {
                w = layer?.LineWeight ?? LineWeightType.Default;
            }

            return (int)w > 0 ? (int)w / 100.0 : DefaultWeightMm;
        }

        private XPen Pen(Entity e)
        {
            var width = Math.Max(WeightMm(e, e.Layer), 0.13) * PtPerMm;
            var pen = new XPen(ink, width) { LineCap = XLineCap.Round, LineJoin = XLineJoin.Round };

            // Тип линии примитива или слоя (ПоСлою); штрихи — в долях толщины пера (так задаёт PDFsharp), точка — короткий штрих.
            var type = e.LineType is { } t && t.Name is not ("ByLayer" or "ByBlock") ? t : e.Layer?.LineType;
            if (type is not null && type.Segments.Any() && !string.Equals(type.Name, "Continuous", StringComparison.OrdinalIgnoreCase))
            {
                var mm = DashPatternMm(type, ltScale, e.LineTypeScale > 0 ? e.LineTypeScale : 1, scale);
                if (mm.Sum() > 0.5)
                {
                    pen.DashStyle = XDashStyle.Custom;
                    pen.DashPattern = [.. mm.Select(x => Math.Max(x, 0.1) * PtPerMm / width)];
                }
            }

            return pen;
        }

        public void Draw(Entity e, Affine t) => Draw(e, t, XColors.Black);

        private void Draw(Entity e, Affine t, XColor parent)
        {
            if (e.Layer is { IsOn: false } || e.IsInvisible)
            {
                return;
            }

            ink = Ink(e, parent);

            switch (e)
            {
                case Line l:
                    g.DrawLine(Pen(e), P(t, l.StartPoint.X, l.StartPoint.Y), P(t, l.EndPoint.X, l.EndPoint.Y));
                    break;
                case Circle c and not Arc:
                    DrawArc(e, t, c.Center.X, c.Center.Y, c.Radius, 0, 2 * Math.PI);
                    break;
                case Arc a:
                    var end = a.EndAngle < a.StartAngle ? a.EndAngle + (2 * Math.PI) : a.EndAngle;
                    DrawArc(e, t, a.Center.X, a.Center.Y, a.Radius, a.StartAngle, end);
                    break;
                case LwPolyline pl:
                    DrawPolyline(e, t, pl);
                    break;
                case TextEntity text:
                    DrawText(t, text);
                    break;
                case MText mtext:
                    DrawMText(t, mtext);
                    break;
                case Insert ins:
                    DrawInsert(t, ins);
                    break;
                default:
                    Skipped.Add(e.ObjectName);
                    break;
            }
        }

        // Дуга — ломаной с шагом не больше 5°: после аффинного преобразования окружность может стать эллипсом.
        private void DrawArc(Entity e, Affine t, double cx, double cy, double r, double from, double to)
        {
            var n = Math.Max(8, (int)Math.Ceiling((to - from) / (Math.PI / 36)));
            var pts = new XPoint[n + 1];
            for (var i = 0; i <= n; i++)
            {
                var a = from + ((to - from) * i / n);
                pts[i] = P(t, cx + (r * Math.Cos(a)), cy + (r * Math.Sin(a)));
            }

            g.DrawLines(Pen(e), pts);
        }

        private void DrawPolyline(Entity e, Affine t, LwPolyline pl)
        {
            var v = pl.Vertices;
            if (v.Count < 2)
            {
                return;
            }

            var pts = new List<XPoint> { P(t, v[0].Location.X, v[0].Location.Y) };
            var count = pl.IsClosed ? v.Count : v.Count - 1;
            for (var i = 0; i < count; i++)
            {
                var a = v[i];
                var b = v[(i + 1) % v.Count];
                if (Math.Abs(a.Bulge) > 1e-9)
                {
                    // Дуга по выпуклости: центральный угол 4·atan(bulge).
                    var theta = 4 * Math.Atan(a.Bulge);
                    var (dx, dy) = (b.Location.X - a.Location.X, b.Location.Y - a.Location.Y);
                    var chord = Math.Sqrt((dx * dx) + (dy * dy));
                    var r = chord / (2 * Math.Sin(theta / 2));
                    var (mx, my) = ((a.Location.X + b.Location.X) / 2, (a.Location.Y + b.Location.Y) / 2);
                    var h = r * Math.Cos(theta / 2);
                    var (cx, cy) = (mx - (dy / chord * h), my + (dx / chord * h));
                    var start = Math.Atan2(a.Location.Y - cy, a.Location.X - cx);
                    var n = Math.Max(4, (int)Math.Ceiling(Math.Abs(theta) / (Math.PI / 36)));
                    for (var k = 1; k <= n; k++)
                    {
                        var ang = start + (theta * k / n);
                        pts.Add(P(t, cx + (Math.Abs(r) * Math.Cos(ang)), cy + (Math.Abs(r) * Math.Sin(ang))));
                    }
                }
                else
                {
                    pts.Add(P(t, b.Location.X, b.Location.Y));
                }
            }

            g.DrawLines(Pen(e), pts.ToArray());
        }

        private XFont Font(double heightModel, Affine t)
        {
            // Высота текста DXF — высота прописной буквы; размер шрифта ≈ высота / 0,7.
            var mm = heightModel * t.ScaleFactor / scale;
            return new XFont(family, Math.Max(mm / 0.7 * PtPerMm, 1));
        }

        private void Text(Affine t, string value, double x, double y, double height, double rotation, double widthFactor, double ax, double ay)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var font = Font(height, t);
            var at = P(t, x, y);
            var angle = -(rotation + t.Angle) * 180 / Math.PI;
            var size = g.MeasureString(value, font);
            var capPt = height * t.ScaleFactor / scale * PtPerMm;
            var state = g.Save();
            g.TranslateTransform(at.X, at.Y);
            g.RotateTransform(angle);
            g.ScaleTransform(widthFactor > 0 ? widthFactor : 1, 1);

            // ax: 0 — слева, 0,5 — по центру, 1 — справа; ay: 0 — по базовой линии, 0,5 — по середине прописных, 1 — по верху.
            g.DrawString(value, font, new XSolidBrush(ink), new XPoint(-size.Width * ax, capPt * ay), XStringFormats.BaseLineLeft);
            g.Restore(state);
        }

        private void DrawText(Affine t, TextEntity e)
        {
            var aligned = e.HorizontalAlignment != TextHorizontalAlignment.Left || e.VerticalAlignment != TextVerticalAlignmentType.Baseline;
            var p = aligned ? e.AlignmentPoint : e.InsertPoint;
            var ax = e.HorizontalAlignment switch
            {
                TextHorizontalAlignment.Center or TextHorizontalAlignment.Middle => 0.5,
                TextHorizontalAlignment.Right => 1.0,
                _ => 0.0,
            };
            var ay = e.HorizontalAlignment == TextHorizontalAlignment.Middle ? 0.5 : e.VerticalAlignment switch
            {
                TextVerticalAlignmentType.Middle => 0.5,
                TextVerticalAlignmentType.Top => 1.0,
                TextVerticalAlignmentType.Bottom => -0.3,
                _ => 0.0,
            };
            Text(t, e.Value, p.X, p.Y, e.Height, e.Rotation, e.WidthFactor, ax, ay);
        }

        private void DrawMText(Affine t, MText e)
        {
            var lines = Wrap(Plain(e.Value), e.Height, e.RectangleWidth, t);
            var rotation = e.Rotation != 0 ? e.Rotation : Math.Atan2(e.AlignmentPoint.Y, e.AlignmentPoint.X);
            if (e.AlignmentPoint.X == 0 && e.AlignmentPoint.Y == 0)
            {
                rotation = e.Rotation;
            }

            var step = e.Height * 1.667 * (e.LineSpacing > 0 ? e.LineSpacing : 1);
            var name = e.AttachmentPoint.ToString();
            var ax = name.EndsWith("Center", StringComparison.Ordinal) ? 0.5 : name.EndsWith("Right", StringComparison.Ordinal) ? 1.0 : 0.0;
            var total = ((lines.Count - 1) * step) + e.Height;

            // Верх блока строк относительно точки вставки.
            var top = name.StartsWith("Top", StringComparison.Ordinal) ? 0 : name.StartsWith("Middle", StringComparison.Ordinal) ? total / 2 : total;
            var (cos, sin) = (Math.Cos(rotation), Math.Sin(rotation));
            for (var i = 0; i < lines.Count; i++)
            {
                // Базовая линия i-й строки — ниже верха на высоту букв и i интервалов (в осях текста).
                var dy = top - e.Height - (i * step);
                var x = e.InsertPoint.X - (sin * dy);
                var y = e.InsertPoint.Y + (cos * dy);
                Text(t, lines[i], x, y, e.Height, rotation, 1, ax, 0);
            }
        }

        private List<string> Wrap(string text, double height, double width, Affine t)
        {
            var result = new List<string>();
            var font = Font(height, t);
            var limit = width * t.ScaleFactor / scale * PtPerMm;
            foreach (var paragraph in text.Split('\n'))
            {
                if (width <= 0)
                {
                    result.Add(paragraph);
                    continue;
                }

                var line = new StringBuilder();
                foreach (var word in paragraph.Split(' '))
                {
                    var candidate = line.Length == 0 ? word : line + " " + word;
                    if (line.Length > 0 && g.MeasureString(candidate, font).Width > limit)
                    {
                        result.Add(line.ToString());
                        line.Clear().Append(word);
                    }
                    else
                    {
                        line.Clear().Append(candidate);
                    }
                }

                result.Add(line.ToString());
            }

            return result;
        }

        private void DrawInsert(Affine t, Insert ins)
        {
            var block = ins.Block;
            var bp = block.BlockEntity.BasePoint;
            var (cos, sin) = (Math.Cos(ins.Rotation), Math.Sin(ins.Rotation));
            var (sx, sy) = (ins.XScale, ins.YScale);

            // Точка блока p → вставка + R·S·(p − база).
            var local = new Affine(cos * sx, sin * sx, -sin * sy, cos * sy, 0, 0);
            local = new Affine(local.A, local.B, local.C, local.D,
                ins.InsertPoint.X - ((local.A * bp.X) + (local.C * bp.Y)),
                ins.InsertPoint.Y - ((local.B * bp.X) + (local.D * bp.Y)));
            var inner = local.Then(t);
            var insertInk = ink;
            foreach (var e in block.Entities)
            {
                Draw(e, inner, insertInk);
            }
        }
    }

    /// <summary>Текст MText без кодов форматирования: \P — перенос строки, {\f…;}, \H…; и т. п. — убираются.</summary>
    public static string Plain(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var s = value.Replace("\\P", "\n", StringComparison.Ordinal);
        s = Codes().Replace(s, "");
        return s.Replace("{", "", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal).Replace("\\~", " ", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\\[A-Za-z][^;\\{}]*;|\\[LlOoKk]")]
    private static partial Regex Codes();
}
