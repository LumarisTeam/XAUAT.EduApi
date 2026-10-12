using System.Xml;
using System.Xml.Linq;
using System.Text.Json;
using EduApi.Data.Models;

namespace XAUAT.EduApi.Services;

/// <summary>
/// 微专业课表 XML 解析：接口 /student/for-std/adminclass-course-table/print-data，
/// 根节点 AdminclassTableExtVm，输出与 CourseActivity 对齐。
/// </summary>
public static class MicroCourseTableParser
{
    private const string RootElementName = "AdminclassTableExtVm";

    /// <summary>兼容两种返回格式：XML（带 Accept: application/xml）与 JSON（默认）。</summary>
    public static List<CourseActivity> Parse(string content)
    {
        var trimmed = content.TrimStart();
        return trimmed.StartsWith('{') ? ParseJson(content) : ParseXml(content);
    }

    private static List<CourseActivity> ParseJson(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"微专业课表返回内容不是有效 JSON: {exception.Message}", exception);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("activities", out var activities)
                || activities.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return activities.EnumerateArray().Select(ToCourseActivity).ToList();
        }
    }

    private static CourseActivity ToCourseActivity(JsonElement activity) => new()
    {
        WeekIndexes = ReadJsonIntList(activity, "weekIndexes"),
        Teachers = ReadJsonStringList(activity, "teachers"),
        Campus = ReadJsonText(activity, "campus"),
        Room = ReadJsonText(activity, "room"),
        CourseName = ReadJsonText(activity, "courseName"),
        CourseCode = ReadJsonText(activity, "courseCode"),
        Weekday = ReadJsonInt(activity, "weekday"),
        StartUnit = ReadJsonInt(activity, "startUnit"),
        EndUnit = ReadJsonInt(activity, "endUnit"),
        Credits = ReadJsonText(activity, "credits"),
        LessonId = ReadJsonText(activity, "lessonId"),
    };

    private static List<int> ReadJsonIntList(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element))
        {
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            var single = ReadJsonInt(element);
            return single is null ? [] : [single.Value];
        }

        var result = new List<int>();
        foreach (var item in element.EnumerateArray())
        {
            var value = ReadJsonInt(item);
            if (value is not null)
            {
                result.Add(value.Value);
            }
        }

        return result;
    }

    private static string[] ReadJsonStringList(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element))
        {
            return [];
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            return element
                .EnumerateArray()
                .Select(ReadJsonText)
                .Where(value => value.Length > 0)
                .ToArray();
        }

        var single = ReadJsonText(element);
        return single.Length > 0 ? [single] : [];
    }

    private static int ReadJsonInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var element) ? ReadJsonInt(element) ?? 0 : 0;

    private static int? ReadJsonInt(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(element.GetString(), out var value) => value,
            _ => null,
        };

    private static string ReadJsonText(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var element) ? ReadJsonText(element) : "";

    private static string ReadJsonText(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };

    private static List<CourseActivity> ParseXml(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException exception)
        {
            throw new InvalidOperationException($"微专业课表返回内容不是有效 XML: {exception.Message}", exception);
        }

        var root = document.Root ?? throw new InvalidOperationException("微专业课表返回内容为空");
        if (!string.Equals(root.Name.LocalName, RootElementName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"未识别的微专业课表 XML 根节点: {root.Name.LocalName}");
        }

        var container = Child(root, "activities");
        if (container is null)
        {
            return [];
        }

        return container
            .Elements()
            .Where(element => string.Equals(element.Name.LocalName, "activities", StringComparison.Ordinal))
            .Select(ToCourseActivity)
            .ToList();
    }

    private static CourseActivity ToCourseActivity(XElement activity) => new()
    {
        WeekIndexes = ReadWeekIndexes(activity),
        Teachers = ReadRepeatedValues(activity, "teachers"),
        Campus = ReadValue(activity, "campus"),
        Room = ReadValue(activity, "room"),
        CourseName = ReadValue(activity, "courseName"),
        CourseCode = ReadValue(activity, "courseCode"),
        Weekday = ReadInt(activity, "weekday") ?? 0,
        StartUnit = ReadInt(activity, "startUnit") ?? 0,
        EndUnit = ReadInt(activity, "endUnit") ?? 0,
        Credits = ReadValue(activity, "credits"),
        LessonId = ReadValue(activity, "lessonId"),
    };

    private static List<int> ReadWeekIndexes(XElement activity)
    {
        var container = Child(activity, "weekIndexes");
        if (container is null)
        {
            return [];
        }

        var result = new List<int>();
        foreach (var element in container.Elements())
        {
            if (!string.Equals(element.Name.LocalName, "weekIndexes", StringComparison.Ordinal))
            {
                continue;
            }

            if (TryParseInt(element.Value, out var week))
            {
                result.Add(week);
            }
        }

        // 兼容只有单个值、没有嵌套子节点的写法。
        if (result.Count == 0 && TryParseInt(container.Value, out var single))
        {
            result.Add(single);
        }

        return result;
    }

    private static string[] ReadRepeatedValues(XElement parent, string containerName)
    {
        var container = Child(parent, containerName);
        if (container is null)
        {
            return [];
        }

        var values = container
            .Elements()
            .Where(element => string.Equals(element.Name.LocalName, containerName, StringComparison.Ordinal))
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0)
            .ToArray();

        if (values.Length > 0)
        {
            return values;
        }

        var direct = container.Value.Trim();
        return direct.Length > 0 ? [direct] : [];
    }

    private static string ReadValue(XElement parent, string name) =>
        Child(parent, name)?.Value.Trim() ?? "";

    private static int? ReadInt(XElement parent, string name)
    {
        var text = Child(parent, name)?.Value;
        return TryParseInt(text, out var value) ? value : null;
    }

    private static bool TryParseInt(string? text, out int value) =>
        int.TryParse(text?.Trim(), out value);

    private static XElement? Child(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(
            element => string.Equals(element.Name.LocalName, name, StringComparison.Ordinal));
}
