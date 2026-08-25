using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace EduApi.Data.Models;

[Serializable]
public class SemesterItem
{
    [MaxLength(64)] public string Value { get; set; } = "";
    [MaxLength(64)] public string Text { get; set; } = "";

    public override string ToString()
    {
        return $"{Text}:{Value}";
    }

    public static SemesterItem Default => new()
    {
        Value = "361",
        Text = "2026-2027-1"
    };
}

[Serializable]
public class SemesterResult
{
    public List<SemesterItem?> Data { get; } = [];

    public void Parse(string html)
    {
        // 检查是否登录
        if (!Regex.IsMatch(html, "学生成绩"))
        {
            return;
        }

        // 使用正则表达式匹配所有semester选项
        var regex = new Regex("<option value=\"(.*?)\">(.*?)</option");
        var matches = regex.Matches(html);

        foreach (Match match in matches)
        {
            if (match.Groups.Count != 3)
            {
                continue;
            }

            var text = match.Groups[2].Value;
            if (text == "...")
            {
                continue;
            }

            Data.Add(new SemesterItem
            {
                Value = match.Groups[1].Value,
                Text = text
            });
        }
    }
}