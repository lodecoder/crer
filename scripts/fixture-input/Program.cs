using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

if (args.Length != 4 || !int.TryParse(args[2], out var port) || port is < 1 or > 65535 || args[3] is not ("play" or "run"))
{
    Console.Error.WriteLine("入力コピーの準備には <元YAML> <証跡ディレクトリ> <port> <play|run> を指定してください。");
    return 2;
}
var preparation = new FixtureInputPreparation(Path.GetFullPath(args[1]), port);
try
{
    var path = preparation.Prepare(Path.GetFullPath(args[0]), args[3] == "run");
    Console.WriteLine(JsonSerializer.Serialize(new { Path = path, Files = preparation.Files }));
    return 0;
}
catch (Exception error)
{
    preparation.Cleanup();
    Console.Error.WriteLine($"fixture入力コピーを準備できません。元入力の親ディレクトリに一時ファイルを作成する権限を確認してください: {error.Message}");
    return 2;
}

sealed record PreparedFile(string Source, string Working, string Evidence);

sealed class FixtureInputPreparation(string evidence, int port)
{
    private readonly string id = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, string> prepared = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PreparedFile> files = [];
    private static readonly Regex Origin = new(@"^http://(?:127\.0\.0\.1|localhost):8080(?=[/?#]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public IReadOnlyList<PreparedFile> Files => files;

    public string Prepare(string source, bool plan)
    {
        if (prepared.TryGetValue(source, out var existing)) return existing;
        var stream = new YamlStream();
        stream.Load(new StringReader(File.ReadAllText(source)));
        if (stream.Documents.Count != 1) throw new FormatException($"入力にはYAML文書が一つ必要です: {source}");
        var ordinal = prepared.Count;
        var working = Path.Combine(Path.GetDirectoryName(source)!, $".crer-fixture-{id}-{ordinal}.yaml");
        prepared.Add(source, working);
        var root = Clone(stream.Documents[0].RootNode, source, plan, new HashSet<YamlNode>(ReferenceEqualityComparer.Instance));
        using var writer = new StringWriter();
        new YamlStream(new YamlDocument(root)).Save(writer, false);
        Directory.CreateDirectory(evidence);
        var snapshot = Path.Combine(evidence, $"{ordinal:D4}.yaml");
        // 元ファイルや既存コピーを上書きしない。相対templateパスは元と同じ親で解決する。
        using (var output = new FileStream(working, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            files.Add(new(source, working, snapshot));
            using var text = new StreamWriter(output, new UTF8Encoding(false));
            text.Write(writer.ToString());
        }
        File.Copy(working, snapshot, false);
        return working;
    }

    private YamlNode Clone(YamlNode node, string source, bool plan, HashSet<YamlNode> ancestors, string? key = null)
    {
        if (!ancestors.Add(node) || ancestors.Count > 64) throw new FormatException("循環するYAML aliasや64階層を超える入力は使用できません。");
        try
        {
            if (node is YamlScalarNode scalar)
            {
                var value = scalar.Value;
                if (plan && key == "scenario" && value is not null)
                {
                    var scenario = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, value));
                    value = Path.GetRelativePath(Path.GetDirectoryName(source)!, Prepare(scenario, false));
                }
                else if (value is not null)
                    value = Origin.Replace(value, match => match.Value[..match.Value.LastIndexOf(':')] + ":" + port);
                return new YamlScalarNode(value) { Style = scalar.Style, Tag = scalar.Tag };
            }
            if (node is YamlSequenceNode sequence)
                return new YamlSequenceNode(sequence.Select(child => Clone(child, source, plan, ancestors))) { Style = sequence.Style, Tag = sequence.Tag };
            if (node is YamlMappingNode mapping)
            {
                var result = new YamlMappingNode { Style = mapping.Style, Tag = mapping.Tag };
                foreach (var pair in mapping.Children)
                    result.Add(Clone(pair.Key, source, false, ancestors), Clone(pair.Value, source, plan, ancestors, (pair.Key as YamlScalarNode)?.Value));
                return result;
            }
            throw new FormatException("未対応のYAMLノードです。");
        }
        finally { ancestors.Remove(node); }
    }

    public void Cleanup()
    {
        // 作成に成功して所有を記録したパスだけを削除する。
        foreach (var file in files)
            try { File.Delete(file.Working); } catch { }
    }
}
