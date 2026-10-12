using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Olve.AgentRuntimeManager.Sessions.Activities;

/// <summary>
/// What a tool call is about, in short: its <c>description</c> (unless blank), else its
/// <c>command</c> (without set-up: <c>cd</c>, <c>export</c>, <c>eval</c>), <c>file_path</c>,
/// <c>pattern</c> or <c>url</c>, else its input as JSON, written as JavaScript's
/// <c>JSON.stringify</c> writes it. The same rules as the web UI's session page (<c>gist</c> in
/// <c>src/frontend/src/sessions/conversation.ts</c>); both are tested against
/// <c>tool-gist.cases.json</c> next to it, so they can't drift apart. At most
/// <see cref="MaxLength"/> characters: a huge input is never written out whole.
/// </summary>
public static partial class ToolGist
{
    public const int MaxLength = 1024;

    public static string Of(JsonElement? input)
    {
        string? Text(string key)
        {
            if (input is not { ValueKind: JsonValueKind.Object } i || !i.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            try
            {
                return value.GetString();
            }
            catch (InvalidOperationException)
            {
                return null; // not valid UTF-16 (a lone surrogate)
            }
        }

        var gist = (Text("description") is { } description && !string.IsNullOrWhiteSpace(description) ? description : null)
            ?? (Text("command") is { } command ? Shell(command) : null)
            ?? Text("file_path")
            ?? Text("pattern")
            ?? Text("url")
            ?? Json(input);
        return ActivityText.Head(gist, MaxLength);
    }

    /// <summary>A shell command's first line without its set-up, which says nothing about what it does.</summary>
    public static string Shell(string command)
    {
        command = ActivityText.Head(command, ActivityText.MaxShaped);
        var parts = Separator().Split(command.Split('\n')[0]).Where(p => p.Length > 0 && !SetUp().IsMatch(p)).ToList();
        return parts.Count > 0 ? string.Join(" && ", parts) : command;
    }

    /// <summary>The input as <c>JSON.stringify</c> writes it (<c>{}</c> without one), stopping once it's longer than <see cref="MaxLength"/>.</summary>
    private static string Json(JsonElement? input)
    {
        var json = new StringBuilder();
        if (input is { } value)
        {
            Write(value, json);
        }
        else
        {
            json.Append("{}");
        }

        return json.ToString();
    }

    private static void Write(JsonElement value, StringBuilder json)
    {
        if (json.Length > MaxLength)
        {
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                json.Append('{');
                var first = true;
                // JavaScript objects list integer-like keys first, in ascending order, then the others in order.
                var indexed = value.EnumerateObject().Where(p => ArrayIndex(p.Name) is not null).OrderBy(p => ArrayIndex(p.Name)).ToList();
                foreach (var property in indexed.Concat(value.EnumerateObject().Where(p => ArrayIndex(p.Name) is null)))
                {
                    if (json.Length > MaxLength)
                    {
                        break;
                    }

                    json.Append(first ? "" : ",");
                    first = false;
                    String(property.Name, json);
                    json.Append(':');
                    Write(property.Value, json);
                }

                json.Append('}');
                break;
            case JsonValueKind.Array:
                json.Append('[');
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (json.Length > MaxLength)
                    {
                        break;
                    }

                    json.Append(index++ == 0 ? "" : ",");
                    Write(item, json);
                }

                json.Append(']');
                break;
            case JsonValueKind.String:
                try
                {
                    String(value.GetString()!, json);
                }
                catch (InvalidOperationException)
                {
                    json.Append(value.GetRawText());
                }

                break;
            case JsonValueKind.Number:
                json.Append(value.TryGetDouble(out var number) && double.IsFinite(number) ? Number(number) : "null");
                break;
            case JsonValueKind.True:
                json.Append("true");
                break;
            case JsonValueKind.False:
                json.Append("false");
                break;
            default:
                json.Append("null");
                break;
        }
    }

    /// <summary>A string as JavaScript quotes it: <c>"</c>, <c>\</c> and control characters escaped, everything else as it is.</summary>
    private static void String(string text, StringBuilder json)
    {
        json.Append('"');
        foreach (var c in text)
        {
            if (json.Length > MaxLength)
            {
                break;
            }

            json.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }

        json.Append('"');
    }

    /// <summary>
    /// A number as JavaScript writes it: the shortest digits that read back as the same number,
    /// plain from 1e-6 up to 1e21 (<c>1.0</c> as <c>1</c>, <c>1e3</c> as <c>1000</c>), else as <c>1e+21</c>.
    /// </summary>
    private static string Number(double number)
    {
        if (number == 0)
        {
            return "0";
        }

        var shortest = Math.Abs(number).ToString("R", CultureInfo.InvariantCulture);
        var parts = shortest.Split('E');
        var exponent = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        var mantissa = parts[0].Split('.');
        var digits = mantissa[0] + (mantissa.Length > 1 ? mantissa[1] : "");
        // The value is 0.<digits> × 10^point, once leading zeros are gone.
        var point = mantissa[0].Length + exponent;
        var trimmed = digits.TrimStart('0');
        point -= digits.Length - trimmed.Length;
        digits = trimmed.TrimEnd('0');
        var sign = number < 0 ? "-" : "";
        var k = digits.Length;
        if (k <= point && point <= 21)
        {
            return sign + digits + new string('0', point - k);
        }

        if (0 < point && point <= 21)
        {
            return $"{sign}{digits[..point]}.{digits[point..]}";
        }

        if (-6 < point && point <= 0)
        {
            return $"{sign}0.{new string('0', -point)}{digits}";
        }

        var e = point - 1;
        return $"{sign}{digits[0]}{(k > 1 ? "." + digits[1..] : "")}e{(e < 0 ? "-" : "+")}{Math.Abs(e)}";
    }

    /// <summary>The key as an array index (JavaScript lists those first), or null.</summary>
    private static uint? ArrayIndex(string key) =>
        key.Length is > 0 and <= 10 && (key == "0" || key[0] != '0') && key.All(char.IsAsciiDigit)
            && uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < uint.MaxValue
            ? index
            : null;

    [GeneratedRegex(@"\s*(?:&&|;)\s*", RegexOptions.None, 100)]
    private static partial Regex Separator();

    [GeneratedRegex(@"^(cd|export|eval)\b", RegexOptions.None, 100)]
    private static partial Regex SetUp();
}
