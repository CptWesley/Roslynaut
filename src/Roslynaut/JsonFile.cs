using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Roslynaut;

/// <summary>Reads and writes the JSON or JSONC files that agents keep their configuration in.</summary>
internal static class JsonFile
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    
    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // A file that is not JSON throws here, before anything is written. Comments and trailing commas are read, as JSONC files have them, but
    // a write does not keep them.
    public static JsonNode Read(string path)
        => File.Exists(path) && JsonNode.Parse(File.ReadAllText(path), documentOptions: Lenient) is { } node
        ? node
        : new JsonObject();

    // Through a temporary file, so a failed write cannot leave the configuration half written.
    public static void Write(string path, JsonNode json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", json.ToJsonString(Indented));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public static JsonArray Strings(string[] values) => new([.. values.Select(value => JsonValue.Create(value))]);
}
