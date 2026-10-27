using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WaterAuto.Application;

namespace WaterAuto.App;

/// <summary>
/// Окно предпросмотра выпущенных документов без Excel/Word: для спецификации xlsx — таблица первого листа
/// (колонки A, B, C…), для пояснительной записки docx — прокручиваемый текст абзацев. Чтение выполняет
/// <see cref="OutputPreview"/> (Reports), ошибки показываются сообщением в окне, а не падением.
/// Кнопка «Открыть в приложении» запускает файл штатным приложением Windows.
/// </summary>
public partial class PreviewWindow : Window
{
    private const int MaxColumns = 30;
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly string _filePath;

    /// <param name="filePath">Файл выпуска (.xlsx — спецификация, .docx — записка и общие данные).</param>
    public PreviewWindow(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        InitializeComponent();
        _filePath = Path.GetFullPath(filePath);
        Title = $"Предпросмотр: {Path.GetFileName(_filePath)}";
        FileNameText.Text = _filePath;
        FileNameText.ToolTip = _filePath;
        KindText.Text = Path.GetExtension(_filePath).TrimStart('.').ToUpperInvariant();
        LoadPreview();
    }

    private void LoadPreview()
    {
        try
        {
            switch (OutputPreview.Read(_filePath))
            {
                case PreviewTable table:
                    ShowTable(table);
                    break;
                case PreviewText text:
                    ShowText(text);
                    break;
                default:
                    ShowError("Этот формат не поддерживается предпросмотром: доступны .xlsx и .docx.");
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowError($"Не удалось прочитать файл: {ex.Message}");
        }
#pragma warning disable CA1031 // окно предпросмотра не должно падать: причину показываем сообщением
        catch (Exception ex)
        {
            ShowError($"Ошибка предпросмотра ({ex.GetType().Name}): {ex.Message}");
        }
#pragma warning restore CA1031
    }

    // Таблица xlsx: колонки A… по числу колонок первого листа, значения как есть (DataGrid только для чтения).
    private void ShowTable(PreviewTable table)
    {
        TableCard.Visibility = Visibility.Visible;
        if (table.Rows.Count == 0)
        {
            InfoText.Text = "Файл прочитан, но лист пуст — показывать нечего.";
            return;
        }

        var columns = Math.Min(MaxColumns, Math.Max(1, table.Rows.Max(row => row.Count)));
        for (var index = 0; index < columns; index++)
        {
            Table.Columns.Add(new DataGridTextColumn
            {
                Header = ColumnName(index),
                Binding = new Binding($"C{index}"),
                Width = new DataGridLength(ColumnWidth(table.Rows, index)),
                ElementStyle = CellStyle(),
            });
        }

        Table.ItemsSource = table.Rows.Select(row => new PreviewRow(row)).ToList();
        InfoText.Text = string.Create(Ru, $"Таблица: строк {table.Rows.Count}, колонок {columns}.");
    }

    // Ширина колонки по первым строкам: длинный текст не обрезается без нужды, числа — узкие.
    private static double ColumnWidth(IReadOnlyList<IReadOnlyList<string>> rows, int index)
    {
        var header = ColumnName(index).Length;
        var longest = rows.Take(60).Select(row => index < row.Count ? row[index].Length : 0).DefaultIfEmpty(0).Max();
        return Math.Clamp(40 + (Math.Max(header, longest) * 7.0), 70, 340);
    }

    // Ячейка: стиль темы Text.Body, многоточие и полный текст в подсказке (длинные наименования не влезают).
    private Style CellStyle()
    {
        var style = new Style(typeof(TextBlock), (Style)FindResource("Text.Body"));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding()));
        return style;
    }

    // Текст docx: абзацы построчно, пустые — интервалы (как в записке).
    private void ShowText(PreviewText text)
    {
        TextCard.Visibility = Visibility.Visible;
        TextPreview.Text = string.Join(Environment.NewLine, text.Lines);
        InfoText.Text = text.Lines.Count == 0
            ? "Файл прочитан, но текста в документе нет."
            : string.Create(Ru, $"Абзацев: {text.Lines.Count}.");
    }

    private void ShowError(string message)
    {
        TableCard.Visibility = Visibility.Collapsed;
        TextCard.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
        InfoText.Text = "Предпросмотр недоступен.";
    }

    // Имя колонки как в Excel: A…Z, AA…AD (ограничение — 30 первых колонок листа).
    private static string ColumnName(int index)
    {
        var name = "";
        for (var number = index + 1; number > 0; number = (number - 1) / 26)
        {
            name = (char)('A' + ((number - 1) % 26)) + name;
        }

        return name;
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_filePath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            ShowError($"Не удалось открыть файл в приложении: {ex.Message}");
        }
    }

    // Строка таблицы: значения ячеек под именами C0…C29 — так DataGrid не зависит от индексаторов и типов строк.
    private sealed class PreviewRow
    {
        private readonly IReadOnlyList<string> _cells;

        public PreviewRow(IReadOnlyList<string> cells) => _cells = cells;

        public string C0 => At(0);

        public string C1 => At(1);

        public string C2 => At(2);

        public string C3 => At(3);

        public string C4 => At(4);

        public string C5 => At(5);

        public string C6 => At(6);

        public string C7 => At(7);

        public string C8 => At(8);

        public string C9 => At(9);

        public string C10 => At(10);

        public string C11 => At(11);

        public string C12 => At(12);

        public string C13 => At(13);

        public string C14 => At(14);

        public string C15 => At(15);

        public string C16 => At(16);

        public string C17 => At(17);

        public string C18 => At(18);

        public string C19 => At(19);

        public string C20 => At(20);

        public string C21 => At(21);

        public string C22 => At(22);

        public string C23 => At(23);

        public string C24 => At(24);

        public string C25 => At(25);

        public string C26 => At(26);

        public string C27 => At(27);

        public string C28 => At(28);

        public string C29 => At(29);

        private string At(int index) => index < _cells.Count ? _cells[index] : "";
    }
}
