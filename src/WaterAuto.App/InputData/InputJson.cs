using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WaterAuto.App.InputData;

/// <summary>
/// Работа с JSON проекта без ручной правки: поиск и создание вложенных объектов, чтение значений в текст контрола,
/// форматирование чисел в русской записи. Ключи файла, кроме перечисленных в <see cref="InputFields"/>, не трогаются.
/// </summary>
internal static class InputJson
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Объект по пути «design.pumpRoom»; пустая строка — корень; null — объекта нет (или он не объект).</summary>
    public static JsonObject? FindObject(JsonObject root, string path)
    {
        if (path.Length == 0)
        {
            return root;
        }

        JsonNode? node = root;
        foreach (var part in path.Split('.'))
        {
            if (node is not JsonObject current || !current.TryGetPropertyValue(part, out var child) || child is null)
            {
                return null;
            }

            node = child;
        }

        return node as JsonObject;
    }

    /// <summary>Объект по пути с созданием недостающих уровней (для записи значений).</summary>
    public static JsonObject EnsureObject(JsonObject root, string path)
    {
        if (path.Length == 0)
        {
            return root;
        }

        var node = root;
        foreach (var part in path.Split('.'))
        {
            if (node[part] is not JsonObject child)
            {
                child = new JsonObject();
                node[part] = child;
            }

            node = child;
        }

        return node;
    }

    /// <summary>Удаление объекта по пути; например, «design.hydrants» при выключенном переключателе раздела.</summary>
    public static void RemovePath(JsonObject root, string path)
    {
        if (path.Length == 0)
        {
            return;
        }

        var last = path.LastIndexOf('.');
        var parent = last < 0 ? root : FindObject(root, path[..last]);
        parent?.Remove(last < 0 ? path : path[(last + 1)..]);
    }

    /// <summary>Значение поля: сначала в объекте по пути, иначе — в умолчаниях объекта по умолчанию.</summary>
    public static JsonNode? Find(JsonObject root, string path, string key) => FindObject(root, path)?[key];

    /// <summary>Значение JSON в виде текста для контрола: строка как есть, число — в русской записи с запятой.</summary>
    public static string Text(JsonNode? node)
    {
        if (node is null)
        {
            return "";
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>(),
            JsonValueKind.Number => FormatNumber(node.GetValue<double>()),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }

    /// <summary>Число в русской записи без лишних нулей; до 10 значащих цифр, чтобы не терять значения при вводе.</summary>
    public static string FormatNumber(double value) => value.ToString("G10", Ru);
}
