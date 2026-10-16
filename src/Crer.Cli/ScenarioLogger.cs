using System.Text.RegularExpressions;

namespace Crer;

public sealed class ScenarioLogger(string name, string color = "default")
{
    private static readonly Dictionary<string, int> Colors = new()
    { ["default"] = 39, ["black"] = 30, ["red"] = 31, ["green"] = 32, ["yellow"] = 33, ["blue"] = 34, ["magenta"] = 35, ["cyan"] = 36, ["white"] = 37 };
    public static string Format(string name, string message, string color = "default", bool colored = false)
    {
        var prefix = $"[{name.Replace('\r', ' ').Replace('\n', ' ')}]";
        return string.Join('\n', Regex.Split(message, "\r?\n").Select(line =>
            colored && color != "default" ? $"\u001b[{Colors[color]}m{prefix} {line}\u001b[39m" : $"{prefix} {line}"));
    }
    private static bool ColorEnabled(bool error) => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
        && !(error ? Console.IsErrorRedirected : Console.IsOutputRedirected);
    public void Log(string message) => Console.WriteLine(Format(name, message, color, ColorEnabled(false)));
    public void Warn(string message) => Console.Error.WriteLine(Format(name, message, color, ColorEnabled(true)));
}
