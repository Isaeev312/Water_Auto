namespace WaterAuto.Reports;

/// <summary>Титульный лист пояснительной записки. Имена — заполнители, их вписывает студент.</summary>
public sealed record NoteTitle(string Organization, string Title, string Subtitle, string Author, string Supervisor, string CityYear, string ObjectName = "");

/// <summary>Элемент текста раздела.</summary>
public abstract record NoteBlock;

/// <summary>
/// Абзац. Разметка в тексте всех блоков: **полужирный**, *курсив*, _{нижний индекс}, ^{верхний индекс}, \n — перенос строки.
/// </summary>
public sealed record NoteParagraph(string Text) : NoteBlock;

/// <summary>Формула по центру с номером у правого поля («(5.1)») и пояснением «где …» строками ниже.</summary>
public sealed record NoteFormula(string Expression, string Number, IReadOnlyList<string> Legend) : NoteBlock;

/// <summary>Перечисление (ГОСТ 7.32: через тире).</summary>
public sealed record NoteList(IReadOnlyList<string> Items) : NoteBlock;

/// <summary>Таблица с названием над ней («Таблица N — …»).</summary>
public sealed record NoteTable(string Caption, IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows) : NoteBlock;

/// <summary>Незаполненная часть: выделяется цветом, чтобы её нельзя было пропустить при оформлении.</summary>
public sealed record NotePending(string Text) : NoteBlock;

/// <summary>Раздел. Пустой номер — структурный элемент («Введение», «Заключение»): заголовок прописными по центру.</summary>
public sealed record NoteSection(string Number, string Heading, IReadOnlyList<NoteBlock> Blocks);

public sealed record NoteDocument(NoteTitle Title, IReadOnlyList<NoteSection> Sections, IReadOnlyList<string> References);
