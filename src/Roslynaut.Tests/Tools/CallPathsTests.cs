using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>call_paths: chains of calls between two members, through an interface and a lambda.</summary>
public class CallPathsTests
{
    private static readonly Microsoft.CodeAnalysis.Solution Shop = TestSolution.Create("""
        namespace Shop
        {
            public interface IStore { void Save(); }
            public class Db : IStore { public void Save() => Write(); void Write() { } }
            public class Service
            {
                private readonly IStore store = new Db();
                public void Handle() => System.Array.ForEach(new[] { 1 }, _ => store.Save());
                public void Direct() => new Db().Save();
            }
            public class Api
            {
                private readonly Service service = new();
                public void Post() { service.Handle(); service.Direct(); }
                public void Unrelated() { }
            }
        }
        """);

    private static async Task<IReadOnlyList<CallPathsTool.CallPath>> PathsAsync(string from, string to, int maxDepth = 6) =>
        await CallPathsTool.FindAsync(Shop, await SymbolTree.ResolveAsync(Shop, from, CancellationToken.None), await SymbolTree.ResolveAsync(Shop, to, CancellationToken.None),
            maxDepth, top: 5, CancellationToken.None);

    [Test]
    public async Task Paths_RunThroughInterfacesAndLambdas_ShortestFirst()
    {
        var paths = await PathsAsync("Shop.Api.Post", "Shop.Db.Write");

        paths.Select(path => string.Join(" > ", path.Hops.Select(hop => hop.Caller.Name).Append(path.Hops[^1].Callee.Name))).Should().BeEquivalentTo(
            "Post > Handle > Save > Write",
            "Post > Direct > Save > Write");
        var throughInterface = paths.Single(path => path.Hops[1].Caller.Name == "Handle").Hops[1];
        (throughInterface.Via, throughInterface.Call.Line).Should().Be(("Shop.IStore.Save()", 8));
        paths.Single(path => path.Hops[1].Caller.Name == "Direct").Hops[1].Via.Should().BeNull();
    }

    [Test]
    public async Task NoPath_SaysSo()
    {
        var act = () => PathsAsync("Shop.Api.Unrelated", "Shop.Db.Write");

        await act.Should().ThrowAsync<ModelContextProtocol.McpException>().WithMessage("No chain of at most 6 calls*");
    }
}
