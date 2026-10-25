namespace WaterAuto.Application;

/// <summary>Шаблон чертежа для работы с архитектурными проектами (см. <see cref="Export.LayerTemplate"/>).</summary>
public static class DrawingTemplate
{
    public static void Write(string path) => Export.LayerTemplate.Write(path);
}
