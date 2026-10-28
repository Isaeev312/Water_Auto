using System.Windows;

namespace WaterAuto.App.InputData;

/// <summary>
/// Доступ к именованным стилям и кистям дизайн-системы приложения (Themes/*.xaml). Своих стилей окно не заводит:
/// если ресурса ещё нет (тема не загружена), контрол просто остаётся без стиля — окно не падает.
/// </summary>
internal static class InputTheme
{
    // Полное имя нужно из-за соседнего пространства имён WaterAuto.Application (иначе CS0234).
    private static System.Windows.Application? App => System.Windows.Application.Current;

    /// <summary>Именованный стиль из словарей приложения; отсутствующий ключ игнорируется.</summary>
    public static void ApplyStyle(FrameworkElement element, string key)
    {
        if (App?.TryFindResource(key) is Style style)
        {
            element.Style = style;
        }
    }
}
