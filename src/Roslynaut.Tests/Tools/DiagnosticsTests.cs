using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

public class DiagnosticsTests
{
    private static Solution WithAnalyzer(string? globalConfig = null)
    {
        var project = TestSolution.Create("public class Widget { }").Projects.Single()
            .AddAnalyzerReference(new AnalyzerImageReference([new TypeNameAnalyzer()]));
        return globalConfig is null
            ? project.Solution
            : project.AddAnalyzerConfigDocument(".globalconfig", SourceText.From(globalConfig, Encoding.UTF8), filePath: Path.Combine(Path.GetTempPath(), ".globalconfig")).Project.Solution;
    }

    private static async Task<List<string>> Collect(Solution solution, DiagnosticSeverity severity, bool analyzers) =>
        [.. (await DiagnosticsTool.CollectAsync(solution, null, severity, analyzers, new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, System.Collections.Concurrent.ConcurrentQueue<Diagnostic>>(), CancellationToken.None)).Select(entry => $"{entry.Id} {entry.Severity}")];

    [Test]
    public async Task Analyzers_ReportAtTheirDefaultSeverity()
    {
        (await Collect(WithAnalyzer(), DiagnosticSeverity.Info, analyzers: true)).Should().Equal("TST001 Info");
    }

    [Test]
    public async Task Analyzers_CanBeLeftOut()
    {
        (await Collect(WithAnalyzer(), DiagnosticSeverity.Hidden, analyzers: false)).Should().BeEmpty();
    }

    [Test]
    public async Task SeverityFilter_LeavesOutLesserDiagnostics()
    {
        (await Collect(WithAnalyzer(), DiagnosticSeverity.Warning, analyzers: true)).Should().BeEmpty();
    }

    // Severities from analyzer configuration apply, as in a build.
    [Test]
    public async Task AnalyzerConfig_ChangesSeverity()
    {
        var solution = WithAnalyzer("is_global = true\ndotnet_diagnostic.TST001.severity = error\n");

        (await Collect(solution, DiagnosticSeverity.Warning, analyzers: true)).Should().Equal("TST001 Error");
    }


    // TST002 is off by default, and the configuration turns TST001 off.
    [Test]
    public async Task AllRules_TurnsOnRulesThatAreOff()
    {
        var solution = WithAnalyzer("is_global = true\ndotnet_diagnostic.TST001.severity = none\n");

        (await Collect(solution, DiagnosticSeverity.Hidden, analyzers: true)).Should().BeEmpty();
        (await DiagnosticsTool.CollectAsync(solution, null, DiagnosticSeverity.Hidden, true, new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, System.Collections.Concurrent.ConcurrentQueue<Diagnostic>>(), CancellationToken.None, allRules: true))
            .Select(entry => $"{entry.Id} {entry.Severity}").Should().BeEquivalentTo("TST001 Warning", "TST002 Warning");
    }

    // As NuGet picks them: the newest Roslyn folder the compiler can load, language-neutral or C#.
    [Test]
    public void AnalyzerFiles_OfTheNewestLoadableRoslyn_ForCSharp()
    {
        var package = Directory.CreateTempSubdirectory("roslynaut-analyzers-");
        try
        {
            foreach (var file in (string[])["roslyn3.8/cs/Old.dll", "roslyn4.8/cs/CSharp.dll", "roslyn4.8/Neutral.dll", "roslyn4.8/vb/Basic.dll", "roslyn99.0/cs/Future.dll"])
            {
                var path = Path.Combine(package.FullName, "analyzers", "dotnet", file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "");
            }

            DiagnosticsTool.AnalyzerFilesOf(package.FullName).Select(Path.GetFileName).Should().BeEquivalentTo("CSharp.dll", "Neutral.dll");
        }
        finally
        {
            package.Delete(recursive: true);
        }
    }

    // Downloads Meziantou.Analyzer from nuget.org, as the project's package sources say.
    [Test]
    public async Task AnalyzerPackages_RunWithoutAReference()
    {
        var directory = Directory.CreateTempSubdirectory("roslynaut-analyzerpackage-");
        try
        {
            var project = Path.Combine(directory.FullName, "Sample.csproj");
            File.WriteAllText(project, "<Project />");
            var solution = TestSolution.Create("public class C { public bool Same(string a, string b) => a == b; }");

            var withPackage = await DiagnosticsTool.WithAnalyzerPackagesAsync(solution, project, ["Meziantou.Analyzer"], new NuGetService(new WorkspaceService()), CancellationToken.None);

            (await DiagnosticsTool.CollectAsync(withPackage, null, DiagnosticSeverity.Hidden, true, new Dictionary<string, IReadOnlyList<string>>(),
                new Dictionary<string, System.Collections.Concurrent.ConcurrentQueue<Diagnostic>>(), CancellationToken.None, allRules: true))
                .Should().Contain(entry => entry.Id == "MA0006");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
    /// <summary>Reports every named type.</summary>
#pragma warning disable RS1036, RS1038, RS1041, RS2008 // A test analyzer: never loaded by a compiler, and never released.
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class TypeNameAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new("TST001", "Type", "Type {0}", "Test", DiagnosticSeverity.Info, isEnabledByDefault: true);
        private static readonly DiagnosticDescriptor Off = new("TST002", "Type", "Also type {0}", "Test", DiagnosticSeverity.Info, isEnabledByDefault: false);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule, Off];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSymbolAction(symbol => symbol.ReportDiagnostic(Diagnostic.Create(Rule, symbol.Symbol.Locations[0], symbol.Symbol.Name)), SymbolKind.NamedType);
            context.RegisterSymbolAction(symbol => symbol.ReportDiagnostic(Diagnostic.Create(Off, symbol.Symbol.Locations[0], symbol.Symbol.Name)), SymbolKind.NamedType);
        }
    }
#pragma warning restore RS1036, RS1038, RS1041, RS2008
}
