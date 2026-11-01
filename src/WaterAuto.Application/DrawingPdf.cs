using WaterAuto.Export;

namespace WaterAuto.Application;

/// <summary>Чертежи выпуска (DXF) в PDF без AutoCAD (ADR 0015): PDF кладётся рядом с DXF.</summary>
public static class DrawingPdf
{
    /// <summary>Печатает все DXF папки (с подпапками); возвращает список выпущенных PDF. Сообщения — в <paramref name="log"/>.</summary>
    public static IReadOnlyList<string> Export(string folder, string font, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var done = new List<string>();
        foreach (var dxf in Directory.GetFiles(folder, "*.dxf", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var pdf = Path.ChangeExtension(dxf, ".pdf");
            if (DxfPdf.Render(dxf, pdf, font, log))
            {
                done.Add(pdf);
                log(pdf);
            }
        }

        return done;
    }
}
