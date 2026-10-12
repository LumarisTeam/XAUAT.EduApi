using System.Text.RegularExpressions;

namespace XAUAT.EduApi.Services;

/// <summary>统一认证登录页的 input 字段提取（lt / execution / _eventId / pwdEncryptSalt）。</summary>
internal sealed partial class SsoLoginFields
{
    private readonly Dictionary<string, string> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _byId = new(StringComparer.OrdinalIgnoreCase);

    public static SsoLoginFields Parse(string html)
    {
        var fields = new SsoLoginFields();

        foreach (Match tag in InputTagRegex().Matches(html))
        {
            string? name = null;
            string? id = null;
            var value = "";

            foreach (Match attribute in AttributeRegex().Matches(tag.Value))
            {
                var attributeName = attribute.Groups["name"].Value;
                var attributeValue = FirstGroupValue(attribute);

                if (attributeName.Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    name = attributeValue;
                }
                else if (attributeName.Equals("id", StringComparison.OrdinalIgnoreCase))
                {
                    id = attributeValue;
                }
                else if (attributeName.Equals("value", StringComparison.OrdinalIgnoreCase))
                {
                    value = attributeValue;
                }
            }

            if (!string.IsNullOrEmpty(name))
            {
                fields._byName[name] = value;
            }

            if (!string.IsNullOrEmpty(id))
            {
                fields._byId[id] = value;
            }
        }

        return fields;
    }

    public string? ByIdOrName(string key) =>
        _byId.TryGetValue(key, out var byId)
            ? byId
            : _byName.TryGetValue(key, out var byName) ? byName : null;

    private static string FirstGroupValue(Match match)
    {
        foreach (var groupName in new[] { "v1", "v2", "v3" })
        {
            if (match.Groups[groupName].Success)
            {
                return match.Groups[groupName].Value;
            }
        }

        return "";
    }

    [GeneratedRegex("(?is)<input\\b[^>]*>")]
    private static partial Regex InputTagRegex();

    [GeneratedRegex(
        "(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\\s*=\\s*(?:\"(?<v1>[^\"]*)\"|'(?<v2>[^']*)'|(?<v3>[^\\s\"'>]+))")]
    private static partial Regex AttributeRegex();
}
