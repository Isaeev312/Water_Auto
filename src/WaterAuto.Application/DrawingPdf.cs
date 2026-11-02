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
        // DWG — лист поверх подложки (plan.dwg); DXF без рамки (план для проверки) пропускается с сообщением.
        var drawings = Directory.GetFiles(folder, "*.dwg", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(folder, "*.dxf", SearchOption.AllDirectories)
                .Where(f => !File.Exists(Path.ChangeExtension(f, ".dwg"))))
            .Order(StringComparer.Ordinal);
        foreach (var dxf in drawings)
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
