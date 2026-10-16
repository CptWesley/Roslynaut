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
        [.. (await DiagnosticsTool.CollectAsync(solution, null, severity, analyzers, CancellationToken.None)).Select(entry => $"{entry.Id} {entry.Severity}")];

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

    /// <summary>Reports every named type.</summary>
#pragma warning disable RS1036, RS1038, RS1041, RS2008 // A test analyzer: never loaded by a compiler, and never released.
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class TypeNameAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new("TST001", "Type", "Type {0}", "Test", DiagnosticSeverity.Info, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSymbolAction(symbol => symbol.ReportDiagnostic(Diagnostic.Create(Rule, symbol.Symbol.Locations[0], symbol.Symbol.Name)), SymbolKind.NamedType);
        }
    }
#pragma warning restore RS1036, RS1038, RS1041, RS2008
}
