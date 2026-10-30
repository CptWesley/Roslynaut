using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Roslynaut.Tools;

namespace Roslynaut.Tests;

/// <summary>Builds in-memory solutions, so tests run without MSBuild.</summary>
internal static class TestSolution
{
    /// <summary>A class Bar nested in a class Foo in namespace Some.Foo.Package, plus symbols with similar names.</summary>
    public static readonly Solution Nested = Create("""
        namespace Some.Foo.Package
        {
            public class Foo
            {
                public class Bar { public void Baz() { int qux = 0; } }
                public class Barrier { }
            }
        }

        namespace Other { public class Bar { } }
        """);

    public static Solution Create(string code) => new AdhocWorkspace()
        .AddProject("Test", LanguageNames.CSharp)
        .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
        .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
        .AddDocument("Test.cs", SourceText.From(code, Encoding.UTF8))
        .Project.Solution;

    /// <summary>The entry's container plus its name, e.g. "Some.Foo.Package.Foo.Bar".</summary>
    public static string PathOf(SymbolEntry entry) => entry.Container is null ? entry.Name : $"{entry.Container}.{entry.Name}";
}
