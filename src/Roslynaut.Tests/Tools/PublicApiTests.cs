using Microsoft.CodeAnalysis;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

public class PublicApiTests
{
    private const string Code = """
        namespace Lib
        {
            public interface IShape { double Area { get; } }
            public abstract class Shape : IShape { public abstract double Area { get; } protected virtual string Name => "shape"; internal int Hidden; }
            public sealed class Circle : Shape
            {
                public Circle(double radius) => Radius = radius;
                public double Radius { get; init; }
                public override double Area => 3.14 * Radius * Radius;
                public static Circle Unit { get; } = new(1);
                public event System.EventHandler? Changed;
                public const int Sides = 0;
                private void Secret() { }
            }
            public record Point(int X, int Y);
            public enum Color { Red, Green = 5 }
            public static class Extensions
            {
                public static T Pick<T>(this T[] items, int index = 0) where T : class => items[index];
                public static void Fill(ref int value, params int[] values) { }
            }
            public delegate void Handler(string? text);
            public struct Size { public int Width; public readonly int Twice() => Width * 2; }
            internal class Internal { public void M() { } }
        }
        """;

    /// <summary>The public API of the code from source, and from the assembly it compiles to.</summary>
    private static async Task<(IReadOnlyList<string> Source, IReadOnlyList<string> Metadata)> BothAsync(string code)
    {
        var project = TestSolution.Create(code).Projects.Single();
        var path = Path.Combine(Path.GetTempPath(), $"roslynaut-api-{Guid.NewGuid():N}.dll");
        try
        {
            var emit = (await project.GetCompilationAsync())!.Emit(path);
            emit.Success.Should().BeTrue(string.Join("; ", emit.Diagnostics));
            return ([.. PublicApi.Of(await PublicApi.AssemblyOfAsync(project, CancellationToken.None))],
                [.. PublicApi.Of(await PublicApi.AssemblyOfAsync(project, path, CancellationToken.None))]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Source and metadata have to read the same, or every comparison would show differences that are not there.
    [Test]
    public async Task SourceAndCompiledAssembly_HaveTheSameApi()
    {
        var (source, metadata) = await BothAsync(Code);

        source.Should().Equal(metadata);
        source.Should().Contain("public sealed class Lib.Circle : Lib.Shape")
            .And.Contain("public Lib.Circle.Circle(double radius)")
            .And.Contain("public static T Lib.Extensions.Pick<T>(this T[] items, int index = 0) where T : class")
            .And.Contain("protected virtual string Lib.Shape.Name { get; }")
            .And.Contain("public delegate void Lib.Handler(string? text)")
            .And.NotContain(line => line.Contains("Internal") || line.Contains("Hidden") || line.Contains("Secret") || line.Contains("Size.Size()"));
    }

    [Test]
    public async Task Diff_ShowsChangedSignature_AsRemovedAndAdded()
    {
        var (before, _) = await BothAsync(Code);
        var (after, _) = await BothAsync(Code.Replace("public Circle(double radius)", "public Circle(double radius, string label = \"\")").Replace("public enum Color { Red, Green = 5 }", ""));

        var diff = ApiDiffTool.Diff(before, after);

        diff.Removed.Should().BeEquivalentTo("public Lib.Circle.Circle(double radius)", "public enum Lib.Color", "Lib.Color.Red = 0", "Lib.Color.Green = 5");
        diff.Added.Should().Equal("public Lib.Circle.Circle(double radius, string label = \"\")");
    }

    // As code declares them: a type's constraints after its supertypes, without the supertypes' own; a struct's readonly and ref once. A record
    // struct reads as a struct, which is all metadata tells.
    [Test]
    public async Task Types_ReadAsDeclared()
    {
        var (source, metadata) = await BothAsync("""
            namespace Lib
            {
                public interface IRepo<T> where T : class { }
                public class Repo<T> : IRepo<T> where T : class, new() { }
                public readonly struct Fixed { }
                public readonly ref struct Both { }
                public readonly record struct Size(int Width);
            }
            """);

        source.Should().Equal(metadata);
        source.Should().Contain("public interface Lib.IRepo<T> where T : class")
            .And.Contain("public class Lib.Repo<T> : Lib.IRepo<T> where T : class, new()")
            .And.Contain("public readonly struct Lib.Fixed")
            .And.Contain("public readonly ref struct Lib.Both")
            .And.Contain("public readonly struct Lib.Size : System.IEquatable<Lib.Size>");
    }
}
