using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace WaterAuto.Reports;

/// <summary>
/// Пояснительная записка в docx по ГОСТ 7.32: A4, поля 30/15/20/20 мм, Times New Roman 14 пт, интервал 1,5,
/// абзацный отступ 1,25 см, номер страницы внизу по центру. Разделы — «N Заголовок» полужирным с отступа,
/// структурные элементы — прописными по центру; перечисления — через тире; таблица — с названием над ней.
/// </summary>
public static class NoteDocx
{
    public static void Write(string path, NoteDocument note)
    {
        ArgumentNullException.ThrowIfNull(note);
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var body = main.Document.Body!;
        AddStyles(main);

        WriteTitlePage(body, note.Title);
        foreach (var section in note.Sections)
        {
            body.Append(Heading(section));
            foreach (var block in section.Blocks)
            {
                switch (block)
                {
                    case NoteParagraph p:
                        body.Append(Text(p.Text));
                        break;
                    case NoteList l:
                        body.Append(l.Items.Select(i => Text($"– {i}")));
                        break;
                    case NotePending p:
                        body.Append(Text(p.Text, highlight: true));
                        break;
                    case NoteFormula f:
                        body.Append(Formula(f));
                        body.Append(f.Legend.Select(l => Text(l, indent: false)));
                        break;
                    case NoteTable t:
                        body.Append(Text(t.Caption, indent: false));
                        body.Append(Table(t));
                        break;
                }
            }
        }

        body.Append(Heading(new NoteSection("", "Список использованных источников", [])));
        body.Append(note.References.Select((r, i) => Text($"{i + 1} {r}")));

        var footer = main.AddNewPart<FooterPart>();
        footer.Footer = new Footer(new Paragraph(
            new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
            new SimpleField { Instruction = "PAGE" }));
        body.Append(new SectionProperties(
            new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) },
            new PageSize { Width = (UInt32Value)(uint)Mm(210), Height = (UInt32Value)(uint)Mm(297) },
            new PageMargin { Left = (UInt32Value)(uint)Mm(30), Right = (UInt32Value)(uint)Mm(15), Top = Mm(20), Bottom = Mm(20), Header = 709, Footer = 709, Gutter = 0 },
            new TitlePage()));
        main.Document.Save();
    }

    private static int Mm(double mm) => (int)Math.Round(mm * 1440 / 25.4);

    private static void AddStyles(MainDocumentPart main)
    {
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman", ComplexScript = "Times New Roman", EastAsia = "Times New Roman" },
                new FontSize { Val = "28" },
                new FontSizeComplexScript { Val = "28" },
                new Languages { Val = "ru-RU" })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                new SpacingBetweenLines { Line = "360", LineRule = LineSpacingRuleValues.Auto, After = "0" },
                new Justification { Val = JustificationValues.Both }))));
    }

    private static void WriteTitlePage(Body body, NoteTitle t)
    {
        Paragraph Centered(string text, bool bold = false, string? size = null)
        {
            var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            var props = new RunProperties();
            if (bold)
            {
                props.Append(new Bold());
            }

            if (size is not null)
            {
                props.Append(new FontSize { Val = size });
            }

            run.PrependChild(props);
            return new Paragraph(new ParagraphProperties(new Justification { Val = JustificationValues.Center }), run);
        }

        body.Append(Centered(t.Organization));
        body.Append(Enumerable.Range(0, 8).Select(_ => new Paragraph()));
        body.Append(Centered(t.Title, bold: true, size: "32"));
        body.Append(Centered(t.Subtitle));
        body.Append(Enumerable.Range(0, 6).Select(_ => new Paragraph()));
        body.Append(new Paragraph(new ParagraphProperties(new Justification { Val = JustificationValues.Right }), new Run(new Text($"Выполнил: {t.Author}"))));
        body.Append(new Paragraph(new ParagraphProperties(new Justification { Val = JustificationValues.Right }), new Run(new Text($"Руководитель: {t.Supervisor}"))));
        body.Append(Enumerable.Range(0, 6).Select(_ => new Paragraph()));
        body.Append(Centered(t.CityYear));
        body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
    }

    private static Paragraph Heading(NoteSection s)
    {
        var structural = s.Number.Length == 0;
        var text = structural ? s.Heading.ToUpper(System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) : $"{s.Number} {s.Heading}";
        var props = new ParagraphProperties(
            new KeepNext(),
            new SpacingBetweenLines { Before = "240", After = "240", Line = "360", LineRule = LineSpacingRuleValues.Auto },
            structural ? new Justification { Val = JustificationValues.Center } : new Justification { Val = JustificationValues.Left });
        if (!structural)
        {
            props.Append(new Indentation { FirstLine = Mm(12.5).ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        return new Paragraph(props, new Run(new RunProperties(new Bold()), new Text(text)));
    }

    private static Paragraph Text(string text, bool highlight = false, bool indent = true)
    {
        var props = new ParagraphProperties();
        if (indent)
        {
            props.Append(new Indentation { FirstLine = Mm(12.5).ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        var paragraph = new Paragraph(props);
        paragraph.Append(Runs(text, highlight));
        return paragraph;
    }

    // Формула: табуляция по центру поля текста и номер у правого края (ширина поля A4 при полях 30 и 15 мм — 165 мм).
    private static Paragraph Formula(NoteFormula f)
    {
        var width = Mm(165);
        var props = new ParagraphProperties(
            new Tabs(
                new TabStop { Val = TabStopValues.Center, Position = width / 2 },
                new TabStop { Val = TabStopValues.Right, Position = width }),
            new SpacingBetweenLines { Before = "120", After = "120", Line = "360", LineRule = LineSpacingRuleValues.Auto },
            new Justification { Val = JustificationValues.Left });
        var paragraph = new Paragraph(props, new Run(new TabChar()));
        paragraph.Append(Runs(f.Expression, false));
        paragraph.Append(new Run(new TabChar()), new Run(new Text($"({f.Number})")));
        return paragraph;
    }

    /// <summary>Разбор разметки: **полужирный**, *курсив*, _{нижний}, ^{верхний}, \n — перенос строки.</summary>
    private static IEnumerable<Run> Runs(string text, bool highlight)
    {
        var bold = false;
        var italic = false;
        var buffer = new System.Text.StringBuilder();
        Run Make(string value, VerticalPositionValues? position)
        {
            var props = new RunProperties();
            if (bold)
            {
                props.Append(new Bold());
            }

            if (italic)
            {
                props.Append(new Italic());
            }

            if (highlight)
            {
                props.Append(new Highlight { Val = HighlightColorValues.Yellow });
            }

            if (position is { } v)
            {
                props.Append(new VerticalTextAlignment { Val = v });
            }

            return new Run(props, new Text(value) { Space = SpaceProcessingModeValues.Preserve });
        }

        IEnumerable<Run> Flush()
        {
            if (buffer.Length > 0)
            {
                yield return Make(buffer.ToString(), null);
                buffer.Clear();
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                foreach (var r in Flush())
                {
                    yield return r;
                }

                bold = !bold;
                i++;
            }
            else if (c == '*')
            {
                foreach (var r in Flush())
                {
                    yield return r;
                }

                italic = !italic;
            }
            else if ((c == '_' || c == '^') && i + 1 < text.Length && text[i + 1] == '{' && text.IndexOf('}', i) is var end and > 0)
            {
                foreach (var r in Flush())
                {
                    yield return r;
                }

                yield return Make(text[(i + 2)..end], c == '_' ? VerticalPositionValues.Subscript : VerticalPositionValues.Superscript);
                i = end;
            }
            else if (c == '\n')
            {
                foreach (var r in Flush())
                {
                    yield return r;
                }

                yield return new Run(new Break());
            }
            else
            {
                buffer.Append(c);
            }
        }

        foreach (var r in Flush())
        {
            yield return r;
        }
    }

    private static Table Table(NoteTable t)
    {
        var border = new TableBorders(
            new TopBorder { Val = BorderValues.Single, Size = 4 },
            new BottomBorder { Val = BorderValues.Single, Size = 4 },
            new LeftBorder { Val = BorderValues.Single, Size = 4 },
            new RightBorder { Val = BorderValues.Single, Size = 4 },
            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
            new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 });
        var table = new Table(new TableProperties(border, new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }));
        TableRow Row(IEnumerable<string> cells, bool bold) => new(cells.Select(c => new TableCell(new Paragraph(
            new ParagraphProperties(new SpacingBetweenLines { Line = "240", LineRule = LineSpacingRuleValues.Auto }, new Justification { Val = JustificationValues.Left }),
            new Run(bold ? new RunProperties(new Bold(), new FontSize { Val = "24" }) : new RunProperties(new FontSize { Val = "24" }), new Text(c) { Space = SpaceProcessingModeValues.Preserve })))));
        table.Append(Row(t.Header, true));
        table.Append(t.Rows.Select(r => Row(r, false)));
        return table;
    }
}
