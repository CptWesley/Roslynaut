using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Scripting.Hosting;
using Microsoft.CodeAnalysis.Scripting;

namespace Roslynaut.ScriptHost;

/// <summary>
/// Runs a C# script. Standard input: a JSON object with the script's Code and the paths of the assemblies it References.
/// Standard output: a JSON object with what the script wrote (Output), the value of its last expression (Value, Type), or the Error.
/// </summary>
public static class Program
{
    private static readonly string[] Imports = ["System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Text", "System.Threading.Tasks"];

    public static async Task Main()
    {
        var request = JsonSerializer.Deserialize<Request>(await Console.In.ReadToEndAsync())!;
        var (stdout, output) = (Console.Out, new StringWriter());
        Console.SetOut(output);
        Console.SetError(output);

        Result result;
        try
        {
            // On .NET, the default options only reference System.Private.CoreLib; add the rest of the framework the host runs on.
            var framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => path.StartsWith(Path.GetDirectoryName(typeof(object).Assembly.Location)!, StringComparison.OrdinalIgnoreCase));
            var options = ScriptOptions.Default
                .AddReferences(framework.Concat(request.References).Select(path => MetadataReference.CreateFromFile(path)))
                .AddImports(Imports);
            var state = await CSharpScript.RunAsync(request.Code, options);
            var value = state.ReturnValue;
            result = new(output.ToString(), value is null ? null : CSharpObjectFormatter.Instance.FormatObject(value), value?.GetType().FullName, null);
        }
        catch (CompilationErrorException e)
        {
            result = new(output.ToString(), null, null, string.Join('\n', e.Diagnostics));
        }
        catch (Exception e)
        {
            result = new(output.ToString(), null, null, e.ToString());
        }

        Console.SetOut(stdout);
        await stdout.WriteLineAsync(JsonSerializer.Serialize(result));
    }

    private sealed record Request(string Code, string[] References);

    private sealed record Result(string Output, string? Value, string? Type, string? Error);
}
