namespace WaterAuto.App.InputData;

/// <summary>Тип поля мастера исходных данных: определяет контрол и способ записи значения в JSON проекта.</summary>
internal enum FieldKind
{
    Text,
    Number,
    Int,
    NullableNumber,
    NullableInt,
    Bool,
    Choice,
    ChoiceInt,
    Dn,
    RoomGroup,
    Sprinkler,
    PipeStandard,
    PumpSet,
    HydrantRow,
    DrawingPath,
}

/// <summary>Вариант выпадающего списка: что записать в JSON проекта и что показать пользователю.</summary>
internal sealed record ChoiceOption(string Value, string Label);

/// <summary>
/// Поле мастера: путь до объекта JSON проекта (например, «design.pumpRoom»; пустая строка — корень файла),
/// ключ в этом объекте, подпись, тип контрола и подсказка под полем. Ключи и пути повторяют прежнее окно 1:1.
/// <see cref="Advanced"/> — экспертное/необязательное поле: в режиме «только стандартные данные» скрыто,
/// но значение из файла проекта сохраняется и не теряется.
/// </summary>
internal sealed record FieldSpec(
    string Path,
    string Key,
    string Label,
    FieldKind Kind,
    ChoiceOption[]? Options = null,
    string? Hint = null,
    bool Required = false,
    bool Advanced = false)
{
    /// <summary>Уникальный идентификатор поля в окне: «путь|ключ».</summary>
    public string Id => Path + "|" + Key;
}

/// <summary>
/// Карточка раздела: заголовок, поля и необязательная информационная строка внизу.
/// <see cref="Advanced"/> — карточка целиком состоит из экспертных параметров и скрыта в простом режиме.
/// </summary>
internal sealed record CardSpec(string Title, FieldSpec[] Fields, string? Info = null, bool Advanced = false);

/// <summary>
/// Раздел мастера: пункт навигации, карточки и — для необязательных объектов (ВПВ, резервуар) —
/// путь объекта, который удаляется из файла проекта при выключенном переключателе.
/// </summary>
internal sealed record SectionSpec(
    string NavTitle,
    string Description,
    CardSpec[] Cards,
    string? OptionalPath = null,
    string? OptionalLabel = null,
    string? OptionalHint = null);

/// <summary>
/// Полный список разделов и полей мастера исходных данных. Здесь только структура ввода: величины норм и каталогов
/// в код не зашиты, их список приходит из data/ через <see cref="InputSources"/>. Флаг <see cref="FieldSpec.Advanced"/>
/// отделяет стандартные данные объекта (их вводит пользователь) от экспертных настроек, которые обычно не трогают.
/// </summary>
internal static class InputFields
{
    private static readonly ChoiceOption[] ConstructionClasses =
    [
        new("K0", "K0 — непожароопасные"),
        new("K1", "K1 — слабопожароопасные"),
        new("K2", "K2 — умереннопожароопасные"),
        new("K3", "K3 — пожароопасные"),
        new("Unrated", "Unrated — класс не определён"),
    ];

    private static readonly ChoiceOption[] Deflectors =
    [
        new("up", "Розетка вверх"),
        new("down", "Розетка вниз"),
        new("horizontal", "Розетка горизонтально"),
        new("concealed", "Скрытая (утопленная) розетка"),
    ];

    private static readonly ChoiceOption[] FireResistances =
    [
        new("", "Не задано"),
        new("I", "I"),
        new("II", "II"),
        new("III", "III"),
        new("IV", "IV"),
        new("V", "V"),
    ];

    private static readonly ChoiceOption[] Categories =
    [
        new("", "Не задано"),
        new("А", "А"),
        new("Б", "Б"),
        new("В", "В"),
        new("Г", "Г"),
        new("Д", "Д"),
    ];

    private static readonly ChoiceOption[] HazardClasses =
    [
        new("", "Не задано"),
        new("С0", "С0 — непожароопасные"),
        new("С1", "С1 — слабопожароопасные"),
        new("С2", "С2 — умереннопожароопасные"),
        new("С3", "С3 — пожароопасные"),
    ];

    private static readonly ChoiceOption[] Nozzles =
    [
        new("13", "13"),
        new("16", "16"),
        new("19", "19"),
    ];

    private static readonly ChoiceOption[] HoseLengths =
    [
        new("10", "10"),
        new("15", "15"),
        new("20", "20"),
    ];

    private static readonly ChoiceOption[] Stages =
    [
        new("Р", "Р — рабочая документация"),
        new("П", "П — проектная документация"),
    ];

    private static readonly SectionSpec[] AllSections =
    [
        new("Проект",
            "Файл проекта, чертёж плана и оформление выпуска. Данные хранятся в *.waproj — JSON руками править не нужно.",
            [
                new CardSpec("Проект и чертёж",
                    [
                        new FieldSpec("", "name", "Название объекта", FieldKind.Text,
                            Hint: "Титульный лист, основная надпись и имена файлов выпуска."),
                        new FieldSpec("", "drawing", "Чертёж плана (DWG/DXF)", FieldKind.DrawingPath, Required: true,
                            Hint: "Путь от папки проекта; слои ПТ_ по соглашению о слоях (README, разд. 7)."),
                        new FieldSpec("", "font", "Шрифт выпуска", FieldKind.Text, Advanced: true,
                            Hint: "Единый шрифт записки, спецификаций и надписей на чертежах; пусто — шрифт по умолчанию."),
                    ],
                    "Чертёж хранится относительным путём, поэтому папку проекта можно переносить вместе с чертежом. " +
                    "Читаются слои ПТ_КОНТУР, ПТ_КОЛОННЫ, ПТ_БАЛКИ, ПТ_КОРОБА, ПТ_ЗАПРЕТ и ПТ_ВВОД."),
            ]),
        new("Установка",
            "Параметры защищаемых помещений и ороситель. Нормативные величины программа берёт из data/norms по выбранной группе помещений.",
            [
                new CardSpec("Помещение и нормативные параметры",
                    [
                        new FieldSpec("design", "roomGroup", "Группа помещений по прил. А СП 485", FieldKind.RoomGroup,
                            Hint: "Строка табл. 6.1: интенсивность, расчётный расход, площадь, шаг и время работы."),
                        new FieldSpec("design", "constructionClass", "Класс пожарной опасности конструкций", FieldKind.Choice,
                            Options: ConstructionClasses,
                            Hint: "Влияет на применимость спринклерной установки (прил. В СП 485)."),
                        new FieldSpec("design", "maxSpacing", "Шаг оросителей строже табл. 6.1, м", FieldKind.NullableNumber,
                            Advanced: true,
                            Hint: "Требование заказчика строже нормы; пусто — шаг по табл. 6.1."),
                        new FieldSpec("design", "roomHeight", "Высота помещения, м", FieldKind.NullableNumber,
                            Hint: "Для табл. 6.3 и примечания 3 к табл. 6.2; пусто — до 10 м. " +
                                "Можно заполнить по чертежу в карточке «Проверка чертежа»."),
                        new FieldSpec("design", "storageHeight", "Высота складирования, м", FieldKind.NullableNumber,
                            Advanced: true,
                            Hint: "Только для складов (группы 5–7, табл. 6.2)."),
                        new FieldSpec("design", "floorElevation", "Отметка пола этажа, м", FieldKind.NullableNumber,
                            Hint: "Для условия п. 6.2.14; пусто — условие считается выполненным."),
                        new FieldSpec("design", "maxAmbientTemperature", "Максимальная температура среды в зоне оросителей, °C",
                            FieldKind.NullableNumber,
                            Hint: "Нужна для выбора номинальной температуры срабатывания (пп. 6.2.16–6.2.17)."),
                    ]),
                new CardSpec("Ороситель",
                    [
                        new FieldSpec("design", "sprinkler", "Ороситель из каталога", FieldKind.Sprinkler),
                        new FieldSpec("design", "sprinklerName", "Наименование оросителя", FieldKind.Text, Advanced: true,
                            Hint: "Тип изделия для спецификации, например «Ороситель спринклерный водяной»."),
                        new FieldSpec("design", "sprinklerModel", "Модель оросителя", FieldKind.Text, Advanced: true,
                            Hint: "Обозначение по паспорту изделия; заполняется при выборе модели из каталога."),
                        new FieldSpec("design", "sprinklerK", "K оросителя, л/(с·МПа^0,5)", FieldKind.Number, Advanced: true,
                            Hint: "По паспорту изделия; 0 — автоподбор модели из data/catalog/sprinklers.json."),
                        new FieldSpec("design", "sprinklerDeflector", "Положение розетки оросителя", FieldKind.Choice,
                            Options: Deflectors,
                            Hint: "Монтажное положение и признак для автоподбора."),
                    ],
                    "Выбор модели из каталога сам заполняет модель, K и положение розетки. K = 0 — программа переберёт " +
                    "модели каталога и выберет ту, что даёт наименьшие Q и H насоса."),
            ]),
        new("Трубы и скорости",
            "Сортамент труб, скорость подбора диаметров (прил. Б СП 485) и подводящий трубопровод.",
            [
                new CardSpec("Сортамент и крепления",
                    [
                        new FieldSpec("design", "pipeStandard", "Стандарт труб (до DN из поля ниже)", FieldKind.PipeStandard,
                            Hint: "Список стандартов — из data/catalog/documents.json."),
                        new FieldSpec("design", "pipeStandardLarge", "Стандарт труб начиная с DN", FieldKind.PipeStandard,
                            Advanced: true,
                            Hint: "Пусто — все трубы по первому стандарту."),
                        new FieldSpec("design", "pipeStandardLargeFromDn", "DN, с которого действует второй стандарт", FieldKind.Dn,
                            Advanced: true,
                            Hint: "Граница двух стандартов в сортаменте."),
                        new FieldSpec("design", "designVelocity", "Скорость подбора диаметров по Б.5, м/с",
                            FieldKind.NullableNumber, Advanced: true,
                            Hint: "Пусто — значение defaultDesignVelocity из data/catalog/pipe_sizes.json."),
                        new FieldSpec("design", "hangerRodLength", "Длина шпильки подвеса, мм", FieldKind.Number, Advanced: true,
                            Hint: "От перекрытия до хомута; по ней карточка проверки чертежа считает отметку трубопроводов."),
                    ],
                    "Диаметры по сортаменту подбираются по скорости из формулы Б.5; стандарты труб и фасонных частей " +
                    "берутся из ведомости документов data/catalog, а не задаются в коде."),
                new CardSpec("Подводящий трубопровод и сеть",
                    [
                        new FieldSpec("design", "supplyLength", "Длина подводящего трубопровода, м", FieldKind.Number,
                            Hint: "От насоса до точки ввода ПТ_ВВОД. Можно заполнить по чертежу в карточке «Проверка чертежа»."),
                        new FieldSpec("design", "supplyDn", "DN подводящего трубопровода", FieldKind.Dn, Advanced: true,
                            Hint: "Проверяется по предельной скорости (п. 6.7.1.37)."),
                        new FieldSpec("design", "inletReference", "Ссылка в выноске у ввода", FieldKind.Text, Advanced: true,
                            Hint: "Текст у точки ввода на плане."),
                        new FieldSpec("design", "pipeElevation", "Отметка трубопроводов, м", FieldKind.Number,
                            Hint: "Аксонометрия и отметки на плане. Можно заполнить по чертежу в карточке «Проверка чертежа»."),
                        new FieldSpec("design", "dictatingAboveAxis", "Высота диктующего оросителя над осью насоса H, м",
                            FieldKind.Number,
                            Hint: "Для требуемого напора насоса (Б.23–Б.30). Можно заполнить по чертежу в карточке «Проверка чертежа»."),
                        new FieldSpec("design", "ring", "Кольцевой питающий трубопровод", FieldKind.Bool, Advanced: true,
                            Hint: "Расчёт по Б.1.2.16.1: 50 % расхода на полукольцо."),
                        new FieldSpec("design", "ringValvesClosed", "На кольце нормально закрытые разделительные задвижки",
                            FieldKind.Bool, Advanced: true,
                            Hint: "Тогда каждая полуветвь считается на 100 % расхода (Б.1.2.16.1)."),
                        new FieldSpec("design", "maxSprinklersPerBranch", "Предел оросителей на ветви (решение проекта)",
                            FieldKind.NullableInt, Advanced: true,
                            Hint: "СП 485 предела не задаёт; пусто — без проверки."),
                    ]),
            ]),
        new("Насосная станция",
            "Комплектная установка из каталога или автоподбор по расчётным Q и H, помещение насосной по чертежу.",
            [
                new CardSpec("Комплектная установка",
                    [
                        new FieldSpec("design.pumpStation", "setDesignation", "Насосная установка", FieldKind.PumpSet),
                        new FieldSpec("design", "inletPressure", "Давление на входе насоса по ТУ, МПа", FieldKind.Number,
                            Hint: "Гарантированный напор по техусловиям; 0 — без подпора."),
                    ],
                    "Пустое значение — автоподбор: программа выберет комплектную установку по расчётным Q и H. " +
                    "Выбор установки из каталога фиксирует её в спецификации и записке."),
                new CardSpec("Помещение насосной",
                    [
                        new FieldSpec("design.pumpRoom", "floorElevation", "Отметка пола насосной, м", FieldKind.Number),
                        new FieldSpec("design.pumpRoom", "groundElevation", "Отметка уровня земли (отмостки), м", FieldKind.Number,
                            Hint: "Для патрубков ГМ на фасаде (п. 6.10.18)."),
                        new FieldSpec("design.pumpRoom", "inletDepth", "Заглубление ввода от уровня земли, м", FieldKind.Number),
                        new FieldSpec("design.pumpRoom", "height", "Высота помещения до низа перекрытия, м", FieldKind.Number,
                            Hint: "Можно заполнить по чертежу в карточке «Проверка чертежа»."),
                        new FieldSpec("design.pumpRoom", "wallThickness", "Толщина стен, м", FieldKind.Number, Advanced: true),
                        new FieldSpec("design.pumpRoom", "underground", "Помещение подземное или заглублённое", FieldKind.Bool,
                            Hint: "П. 6.10.23: не менее двух дренажных насосов."),
                        new FieldSpec("design.pumpRoom", "pitSize", "Приямок в плане, м", FieldKind.Number, Advanced: true),
                        new FieldSpec("design.pumpRoom", "pitDepth", "Глубина приямка, м", FieldKind.Number, Advanced: true),
                        new FieldSpec("design.pumpRoom", "inletRouteElevation", "Отметка трассы ввода, м",
                            FieldKind.NullableNumber, Advanced: true,
                            Hint: "Пусто — под перекрытием на отметке трубопроводов АУП."),
                        new FieldSpec("design.pumpRoom", "inletRouteClearance", "Отступ трассы ввода от колонн, м",
                            FieldKind.Number, Advanced: true),
                    ],
                    "Число вводов и выпусков задаётся точками на чертеже (слои ПТ_НС_ВВОД и ПТ_НС_ВЫПУСК); программа " +
                    "проверит пп. 6.10.27–6.10.28. DN обвязки подбирается по скорости (п. 6.7.1.37)."),
                new CardSpec("Жокей-насос (решение проектировщика, В-07)",
                    [
                        new FieldSpec("design.jockey", "flowLitersPerSecond", "Расход жокей-насоса, л/с", FieldKind.NullableNumber,
                            Hint: "Норма расход не задаёт (В-07). Пусто — не задано; уточните по опросному листу изготовителя."),
                        new FieldSpec("design.jockey", "startPressureMpa", "Давление включения, МПа", FieldKind.NullableNumber,
                            Hint: "Уставка включения — решение проектировщика, норма её не задаёт (В-07)."),
                        new FieldSpec("design.jockey", "stopPressureMpa", "Давление отключения, МПа", FieldKind.NullableNumber,
                            Hint: "Уставка отключения — решение проектировщика, норма её не задаёт (В-07)."),
                    ],
                    "Расход и уставки жокей-насоса норма не задаёт (вопрос В-07): они принимаются решением проектировщика " +
                    "по опросному листу изготовителя. Пустые поля — не заданы; на расчёт Q/H и спецификацию не влияют.",
                    Advanced: true),
            ]),
        new("ВПВ",
            "Внутренний противопожарный водопровод по СН КР 40-04:2025: признаки здания (табл. Ж.2, Ж.3) и диктующий пожарный кран.",
            [
                new CardSpec("Здание",
                    [
                        new FieldSpec("design.hydrants", "fireResistance", "Степень огнестойкости здания", FieldKind.Choice,
                            Options: FireResistances),
                        new FieldSpec("design.hydrants", "category", "Категория по пожарной опасности", FieldKind.Choice,
                            Options: Categories),
                        new FieldSpec("design.hydrants", "hazardClass", "Класс конструктивной пожарной опасности",
                            FieldKind.Choice, Options: HazardClasses),
                        new FieldSpec("design.hydrants", "volume", "Объём здания (пожарного отсека), тыс. м³", FieldKind.Number),
                        new FieldSpec("design.hydrants", "buildingHeight", "Высота здания, м", FieldKind.Number),
                        new FieldSpec("design.hydrants", "isParking", "Отапливаемая автостоянка закрытого типа",
                            FieldKind.Bool, Hint: "П. 6.4 СНиП 21-02-99* (ADR 0010)."),
                        new FieldSpec("design.hydrants", "zh1Row", "Строка табл. Ж.1 СН КР 40-04:2025", FieldKind.HydrantRow,
                            Hint: "Для жилых и общественных зданий; иначе не применяется."),
                    ],
                    "Признаки здания нужны для выбора расхода воды на внутреннее пожаротушение (табл. Ж.2, Ж.3)."),
                new CardSpec("Пожарные краны и трубопровод",
                    [
                        new FieldSpec("design.hydrants", "roomHeight", "Высота помещения у диктующего ПК, м",
                            FieldKind.NullableNumber, Hint: "Пусто — высота помещения из раздела «Установка»."),
                        new FieldSpec("design.hydrants", "valveDn", "DN клапана пожарного крана", FieldKind.Dn),
                        new FieldSpec("design.hydrants", "nozzle", "Диаметр спрыска ствола, мм", FieldKind.ChoiceInt,
                            Options: Nozzles),
                        new FieldSpec("design.hydrants", "hoseLength", "Длина рукава, м", FieldKind.ChoiceInt,
                            Options: HoseLengths),
                        new FieldSpec("design.hydrants", "aboveAxis", "Отметка клапана диктующего ПК над осью насоса, м",
                            FieldKind.Number),
                        new FieldSpec("design.hydrants", "pipeLength", "Длина трубопровода до диктующего ПК, м",
                            FieldKind.Number),
                        new FieldSpec("design.hydrants", "pipeDn", "DN трубопровода до диктующего ПК", FieldKind.Dn),
                        new FieldSpec("design.hydrants", "extraJet", "Зал с массовым пребыванием людей (на одну струю больше)",
                            FieldKind.Bool, Hint: "П. 7.7."),
                        new FieldSpec("design.hydrants", "reach", "Радиус действия ПК для расстановки, м",
                            FieldKind.NullableNumber, Hint: "Пусто — рукав плюс радиус компактной струи (В-22)."),
                        new FieldSpec("design.hydrants", "place", "Расставлять краны на плане", FieldKind.Bool,
                            Hint: "Выключено — комплект в спецификации на один кран."),
                    ],
                    "Подробные параметры диктующего крана обычно берутся из типового решения. " +
                    "Пустое поле — расчёт по принятому пожарному крану и данным здания.",
                    Advanced: true),
            ],
            OptionalPath: "design.hydrants",
            OptionalLabel: "Учитывать внутренний противопожарный водопровод (совмещённый с АУП)",
            OptionalHint: "Если выключено, данные ВПВ удаляются из проекта и расчёт выпускается только по АУП."),
        new("Резервуар",
            "Резервуар пожарного запаса воды: габариты и мёртвый слой для сравнения с требуемым объёмом.",
            [
                new CardSpec("Резервуар",
                    [
                        new FieldSpec("design.tank", "length", "Длина в плане, м", FieldKind.Number),
                        new FieldSpec("design.tank", "width", "Ширина в плане, м", FieldKind.Number),
                        new FieldSpec("design.tank", "height", "Высота воды, м", FieldKind.Number),
                        new FieldSpec("design.tank", "deadLayer", "Мёртвый слой, м", FieldKind.Number,
                            Hint: "Вода ниже него насосом не используется."),
                    ],
                    "Требуемый объём запаса воды программа сравнивает с объёмом резервуара за вычетом мёртвого слоя."),
            ],
            OptionalPath: "design.tank",
            OptionalLabel: "Сравнивать с резервуаром пользователя",
            OptionalHint: "Если выключено, сравнение требуемого объёма с резервуаром не выполняется."),
        new("Титульный лист",
            "Данные титульного листа пояснительной записки и основной надписи.",
            [
                new CardSpec("Документ",
                    [
                        new FieldSpec("title", "organization", "Организация", FieldKind.Text),
                        new FieldSpec("title", "subject", "Тема", FieldKind.Text),
                        new FieldSpec("title", "document", "Наименование документа", FieldKind.Text),
                        new FieldSpec("title", "code", "Шифр проекта", FieldKind.Text,
                            Hint: "Базовое обозначение; к нему добавляются марки «ПТ» и «ПТ.СО»."),
                        new FieldSpec("title", "stage", "Стадия", FieldKind.Choice, Options: Stages,
                            Hint: "Стадия из графы 6 основной надписи."),
                        new FieldSpec("title", "building", "Наименование здания", FieldKind.Text,
                            Hint: "Графа 3 основной надписи."),
                        new FieldSpec("title", "cityYear", "Город, год", FieldKind.Text),
                    ]),
                new CardSpec("Исполнители",
                    [
                        new FieldSpec("title", "author", "Разработал", FieldKind.Text),
                        new FieldSpec("title", "supervisor", "Руководитель", FieldKind.Text),
                    ],
                    "Пустые подписи основной надписи берутся с титульного листа; должности — по ГОСТ 21.101."),
            ]),
    ];

    /// <summary>Разделы мастера в порядке навигации.</summary>
    public static IReadOnlyList<SectionSpec> All => AllSections;
}
