using Microsoft.CodeAnalysis;
using ModelContextProtocol;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>references, symbol_source, implementations, type_hierarchy and callers.</summary>
public class NavigationTests
{
    private static readonly Solution Shapes = TestSolution.Create("""
        namespace Shapes
        {
            public interface IShape { double Area(); }
            public abstract class Shape : IShape { public abstract double Area(); }
            public class Circle : Shape
            {
                public double Radius;
                public override double Area() => 3.14 * Radius * Radius;
            }
            public sealed class Unit : Circle { }
            public class Square : IShape { public double Side { get; set; } public double Area() => Side * Side; }
            public static class Use
            {
                public static double Total(IShape[] shapes) { var sum = 0.0; foreach (var shape in shapes) sum += shape.Area(); return sum; }
                public static void Grow(Circle circle) { circle.Radius = circle.Radius * 2; circle.Radius++; }
            }
        }
        """);

    private static async Task<IReadOnlyList<ISymbol>> Resolve(string path) => await SymbolTree.ResolveAsync(Shapes, path, CancellationToken.None);

    private static async Task<List<string>> Paths(IEnumerable<SymbolEntry> entries) => [.. entries.Select(TestSolution.PathOf)];

    [Test]
    public async Task References_HaveContainerAndAccess()
    {
        var references = await ReferencesTool.FindAsync(Shapes, await Resolve("Circle.Radius"), CancellationToken.None);

        references.Select(reference => (reference.Container, reference.Access)).Should().BeEquivalentTo(new[]
        {
            ("Shapes.Circle.Area()", "Read"),
            ("Shapes.Circle.Area()", "Read"),
            ("Shapes.Use.Grow(Shapes.Circle)", "Write"),
            ("Shapes.Use.Grow(Shapes.Circle)", "Read"),
            ("Shapes.Use.Grow(Shapes.Circle)", "Write"),
        });
    }

    [Test]
    public async Task References_ToMethod_AreCalls()
    {
        (await ReferencesTool.FindAsync(Shapes, await Resolve("IShape.Area"), CancellationToken.None))
            .Should().ContainSingle().Which.Should().Match<ReferencesTool.ReferenceEntry>(reference =>
                reference.Access == "Call" && reference.Container == "Shapes.Use.Total(Shapes.IShape[])");
    }

    [Test]
    public async Task SymbolSource_ReturnsWholeDeclarationFromStartOfLine()
    {
        var source = (await SymbolSourceTool.GetSourcesAsync(await Resolve("Circle.Area"), CancellationToken.None)).Should().ContainSingle().Subject;

        source.Source.Should().Be("        public override double Area() => 3.14 * Radius * Radius;");
        source.Location.Line.Should().Be(8);
    }

    [Test]
    public async Task SymbolSource_Field_ReturnsItsDeclaration()
    {
        (await SymbolSourceTool.GetSourcesAsync(await Resolve("Circle.Radius"), CancellationToken.None))
            .Should().ContainSingle().Which.Source.Should().Be("        public double Radius;");
    }

    [TestCase("Shapes.IShape", new[] { "Shapes.Shape", "Shapes.Square" })]
    [TestCase("IShape.Area", new[] { "Shapes.Shape.Area", "Shapes.Square.Area" })]
    [TestCase("Shape.Area", new[] { "Shapes.Circle.Area" })]
    public async Task Implementations_IncludeImplementationsAndOverrides(string path, string[] expected)
    {
        (await Paths(await ImplementationsTool.FindAsync(Shapes, await Resolve(path), CancellationToken.None))).Should().Contain(expected);
    }

    [Test]
    public async Task TypeHierarchy_Supertypes_ListsBaseTypesThenInterfaces()
    {
        (await Paths(await TypeHierarchyTool.FindAsync(Shapes, await Resolve("Shapes.Unit"), HierarchyDirection.Supertypes, CancellationToken.None)))
            .Should().Equal("Shapes.Circle", "Shapes.Shape", "System.Object", "Shapes.IShape");
    }

    [TestCase("Shapes.Shape", new[] { "Shapes.Circle", "Shapes.Unit" })]
    [TestCase("Shapes.IShape", new[] { "Shapes.Shape", "Shapes.Circle", "Shapes.Unit", "Shapes.Square" })]
    public async Task TypeHierarchy_Subtypes_AreTransitive(string path, string[] expected)
    {
        (await Paths(await TypeHierarchyTool.FindAsync(Shapes, await Resolve(path), HierarchyDirection.Subtypes, CancellationToken.None)))
            .Should().BeEquivalentTo(expected);
    }

    [Test]
    public async Task TypeHierarchy_OfVariable_UsesItsType()
    {
        (await Paths(await TypeHierarchyTool.FindAsync(Shapes, await Resolve("Use.Grow.circle"), HierarchyDirection.Subtypes, CancellationToken.None)))
            .Should().Equal("Shapes.Unit");
    }

    [Test]
    public async Task Callers_GroupCallsByCallingMember()
    {
        var caller = (await CallersTool.FindAsync(Shapes, await Resolve("Circle.Radius"), CancellationToken.None)).Should().ContainSingle(c => c.Symbol.Name == "Grow").Subject;

        caller.Calls.Should().HaveCount(3);
    }

    // Agents guess paths: a miss names the paths that come closest, for a wrong container, overload or spelling.
    [TestCase("Square.Radius", "No symbol found for 'Square.Radius'. Closest paths: 'Shapes.Circle.Radius'.")]
    [TestCase("Use.Total(Circle[])", "No symbol found for 'Use.Total(Circle[])'. Closest paths: 'Shapes.Use.Total(IShape[])'.")]
    [TestCase("Circle.Aera", "No symbol found for 'Circle.Aera'. Closest paths: 'Shapes.Circle.Area()', *")]
    [TestCase("Shapez", "No symbol found for 'Shapez'. Closest paths: 'Shapes', 'Shapes.Shape', 'Shapes.IShape'.")]
    [TestCase("Hexagon", "No symbol found for 'Hexagon'.")]
    public async Task SymbolPaths_ThatMatchNothing_NameTheClosest(string path, string message) =>
        await FluentActions.Awaiting(() => Resolve(path)).Should().ThrowAsync<McpException>().WithMessage(message);
}
