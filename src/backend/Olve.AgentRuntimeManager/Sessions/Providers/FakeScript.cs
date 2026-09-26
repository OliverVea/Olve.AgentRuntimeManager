using System.Globalization;
using System.Text.RegularExpressions;

namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>
/// What a <see cref="FakeProvider"/> agent does, read from <c>fake:</c> directives anywhere in its
/// prompt (docs/TESTING.md: every per-push test path is LLM-free):
/// <list type="bullet">
///   <item><c>fake:sleep=250ms</c> (or <c>2s</c>, <c>1m</c>): run this long, then complete.</item>
///   <item><c>fake:hang</c>: run until killed (or timed out).</item>
///   <item><c>fake:exit=3</c>: complete with this exit code.</item>
///   <item><c>fake:summary=all_done</c>: complete with this summary (<c>_</c> reads as a space).</item>
///   <item><c>fake:fail=boom</c>: fail with this error (<c>_</c> reads as a space) instead of completing.</item>
/// </list>
/// An unknown directive or a bad value is an error: the agent fails to start.
/// </summary>
public sealed partial record FakeScript(TimeSpan Delay, bool Hang, int ExitCode, string? Summary, string? Failure)
{
    public static FakeScript Parse(string prompt, TimeSpan defaultDelay)
    {
        var script = new FakeScript(defaultDelay, Hang: false, ExitCode: 0, Summary: null, Failure: null);
        foreach (Match match in Directive().Matches(prompt))
        {
            var name = match.Groups["name"].Value;
            var value = match.Groups["value"].Success ? match.Groups["value"].Value : null;
            script = (name, value) switch
            {
                ("hang", null) => script with { Hang = true },
                ("sleep", { } v) => script with { Delay = ParseDuration(v) },
                ("exit", { } v) when int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code) => script with { ExitCode = code },
                ("summary", { } v) => script with { Summary = v.Replace('_', ' ') },
                ("fail", { } v) => script with { Failure = v.Replace('_', ' ') },
                _ => throw new FormatException($"Unknown fake directive '{match.Value}'."),
            };
        }

        return script;
    }

    private static TimeSpan ParseDuration(string value)
    {
        var match = Duration().Match(value);
        if (!match.Success)
        {
            throw new FormatException($"'{value}' is not a duration (e.g. 250ms, 2s, 1m).");
        }

        var amount = int.Parse(match.Groups["amount"].Value, CultureInfo.InvariantCulture);
        return match.Groups["unit"].Value switch
        {
            "ms" => TimeSpan.FromMilliseconds(amount),
            "s" => TimeSpan.FromSeconds(amount),
            _ => TimeSpan.FromMinutes(amount),
        };
    }

    [GeneratedRegex(@"\bfake:(?<name>[a-z]+)(?:=(?<value>\S+))?")]
    private static partial Regex Directive();

    [GeneratedRegex(@"^(?<amount>\d+)(?<unit>ms|s|m)$")]
    private static partial Regex Duration();
}
