using NuGet.Configuration;
using Roslynaut.Tools;

namespace Roslynaut.Tests.EndToEnd;

/// <summary>
/// The code the end-to-end tests run the tools on, written to a temporary directory under git:
/// <list type="bullet">
/// <item>Shapes.slnx, with the library src/Shapes, the console app src/Shapes.App and the NUnit project tests/Shapes.Tests.</item>
/// <item>A local feed with Roslynaut.E2E.Greetings 1.0.0, 2.0.0 and 3.0.0-beta, which depend on Roslynaut.E2E.Base 1.0.0. Their assemblies embed their PDB
/// and source, so their source is at hand without a symbol server. Shapes.App uses Greetings 1.0.0.</item>
/// <item>Problems/Problems.csproj, outside the solution, with one compiler error and one warning.</item>
/// <item>Targets/Targets.csproj, outside the solution, for net10.0 and netstandard2.0.</item>
/// </list>
/// Packages restore into a packages folder of their own, taking what the machine's global packages folder has from there.
/// </summary>
internal sealed class SampleSolution
{
    public const string GreetingsId = "Roslynaut.E2E.Greetings";
    public const string BaseId = "Roslynaut.E2E.Base";

    private SampleSolution(string root) => Root = root;

    public string Root { get; }

    public string Solution => In("Shapes.slnx");

    public string ShapesProject => In("src", "Shapes", "Shapes.csproj");

    public string AppProject => In("src", "Shapes.App", "Shapes.App.csproj");

    public string TestsProject => In("tests", "Shapes.Tests", "Shapes.Tests.csproj");

    public string ProblemsProject => In("Problems", "Problems.csproj");

    public string TargetsProject => In("Targets", "Targets.csproj");

    public string IShapeFile => In("src", "Shapes", "IShape.cs");

    public string ShapeFile => In("src", "Shapes", "Shape.cs");

    public string CircleFile => In("src", "Shapes", "Circle.cs");

    public string RectangleFile => In("src", "Shapes", "Rectangle.cs");

    public string SquareFile => In("src", "Shapes", "Square.cs");

    public string ShapeMathFile => In("src", "Shapes", "ShapeMath.cs");

    public string RegistryFile => In("src", "Shapes", "Internal", "Registry.cs");

    public string ProgramFile => In("src", "Shapes.App", "Program.cs");

    public string SpinnerFile => In("src", "Shapes.App", "Spinner.cs");

    public string TestsFile => In("tests", "Shapes.Tests", "ShapeTests.cs");

    public string AccountFile => In("Problems", "Account.cs");

    public string Feed => In("feed");

    public string Packages => In("packages");

    /// <summary>The build output of Shapes.App, to run.</summary>
    public string AppAssembly => In("src", "Shapes.App", "bin", "Debug", "net10.0", "Shapes.App.dll");

    public string ShapesAssembly => In("src", "Shapes", "bin", "Debug", "net10.0", "Shapes.dll");

    /// <summary>The assembly of Greetings 1.0.0, as restore extracted it.</summary>
    public string GreetingsAssembly => In("packages", GreetingsId.ToLowerInvariant(), "1.0.0", "lib", "net10.0", $"{GreetingsId}.dll");

    public string In(params string[] path) => Path.Combine([Root, .. path]);

    /// <summary>Writes the sample and packs its packages; the solution is not built yet.</summary>
    public static async Task<SampleSolution> CreateAsync()
    {
        var sample = new SampleSolution(Directory.CreateTempSubdirectory("roslynaut-e2e-").FullName);
        sample.Write("nuget.config", $"""
            <configuration>
              <config><add key="globalPackagesFolder" value="packages" /></config>
              <fallbackPackageFolders>
                <clear />
                <add key="machine" value="{SettingsUtility.GetGlobalPackagesFolder(Settings.LoadDefaultSettings(null))}" />
              </fallbackPackageFolders>
              <packageSources>
                <clear />
                <add key="local" value="feed" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="local"><package pattern="Roslynaut.E2E.*" /></packageSource>
                <packageSource key="nuget.org"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        sample.Write(".gitignore", "bin/\nobj/\npackages/\nfeed/\nedits/\n*.binlog\n");
        await sample.PackAsync();

        foreach (var (path, text) in Files)
            sample.Write(path, text);
        return sample;
    }

    /// <summary>Writes a file with \n line ends, whatever the line ends of this source file are where it was checked out.</summary>
    public string Write(string relativePath, string text)
    {
        var path = In(relativePath.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ReplaceLineEndings("\n"));
        return path;
    }

    /// <summary>
    /// A project of its own for a test that changes code, in edits/{name}, with the files given by their names; returns the project's path.
    /// </summary>
    public string NewProject(string name, params (string Name, string Text)[] files)
    {
        var project = Write($"edits/{name}/{name}.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        foreach (var (file, text) in files)
            Write($"edits/{name}/{file}", text);
        return project;
    }

    /// <summary>
    /// A solution of its own in edits/{name}, whose projects One and Two both compile Common/Shared.cs as a linked file, and One also Use.cs:
    /// the file declares its symbols in each project. Returns the solution's path.
    /// </summary>
    public string NewLinkedSolution(string name, string shared, string use)
    {
        foreach (var project in new[] { "One", "Two" })
        {
            Write($"edits/{name}/{project}/{project}.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="..\Common\Shared.cs" Link="Shared.cs" />
                  </ItemGroup>
                </Project>
                """);
        }

        Write($"edits/{name}/Common/Shared.cs", shared);
        Write($"edits/{name}/One/Use.cs", use);
        return Write($"edits/{name}/{name}.slnx", "<Solution>\n  <Project Path=\"One/One.csproj\" />\n  <Project Path=\"Two/Two.csproj\" />\n</Solution>\n");
    }

    /// <summary>The location tools report for the first occurrence of a text in a file: where the text starts, with its whole line.</summary>
    public SourceLocation Location(string file, string text, int occurrence = 1)
    {
        var (line, column) = Find(file, text, occurrence);
        return new(file, line, column, File.ReadAllLines(file)[line - 1].Trim());
    }

    /// <summary>The 1-based line and column where the first occurrence of a text starts in a file of the sample.</summary>
    public (int Line, int Column) Find(string file, string text, int occurrence = 1)
    {
        var content = File.ReadAllText(file);
        var index = -1;
        for (var i = 0; i < occurrence; i++)
        {
            index = content.IndexOf(text, index + 1, StringComparison.Ordinal);
            if (index < 0)
                throw new ArgumentException($"'{text}' does not occur {occurrence} times in {file}.");
        }

        var lineStart = content.LastIndexOf('\n', Math.Max(index - 1, 0)) + 1;
        return (content[..index].Count(c => c == '\n') + 1, index - lineStart + 1);
    }

    public int LineOf(string file, string text, int occurrence = 1) => Find(file, text, occurrence).Line;

    /// <summary>Starts the built Shapes.App so that it waits for input while a thread of it spins; kill it when done.</summary>
    public System.Diagnostics.Process StartWaitingApp()
    {
        var app = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DotnetCli.Executable)
        {
            ArgumentList = { "exec", AppAssembly, "wait" },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        })!;
        while (app.StandardOutput.ReadLine() is { } line && line != "Waiting")
        {
        }

        return app;
    }

    /// <summary>Commits the sample, so affected_tests has a commit to compare with.</summary>
    public async Task CommitAsync()
    {
        await GitAsync("init", "-q");
        await GitAsync("add", ".");
        await GitAsync("-c", "user.name=Roslynaut", "-c", "user.email=roslynaut@example.com", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "Sample");
    }

    public async Task GitAsync(params string[] arguments)
    {
        var (exitCode, output) = await DotnetCli.RunAsync("git", ["-C", Root, .. arguments], TimeSpan.FromMinutes(1), CancellationToken.None);
        exitCode.Should().Be(0, output);
    }

    public async Task DotnetAsync(params string[] arguments)
    {
        var (exitCode, output) = await DotnetCli.RunAsync(arguments, TimeSpan.FromMinutes(10), CancellationToken.None, workingDirectory: Root);
        exitCode.Should().Be(0, output);
    }

    public void Delete()
    {
        // git marks its objects read-only; a process that still runs can hold a file for a moment.
        for (var attempt = 0; Directory.Exists(Root); attempt++)
        {
            try
            {
                foreach (var file in new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories))
                    file.Attributes = FileAttributes.Normal;
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 10)
            {
                Thread.Sleep(500);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                TestContext.Progress.WriteLine($"Could not delete {Root}: {e.Message}");
                return;
            }
        }
    }

    /// <summary>Base 1.0.0, then Greetings 1.0.0, 2.0.0 and 3.0.0-beta: 2.0.0 makes Hello obsolete, removes Bye and adds Hi; 3.0.0-beta is the same.</summary>
    private async Task PackAsync()
    {
        var @base = Write($"packages-src/Base/{BaseId}.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <PackageLicenseExpression>MIT</PackageLicenseExpression>
              </PropertyGroup>
            </Project>
            """);
        Write("packages-src/Base/Punctuation.cs", """
            namespace Roslynaut.E2E.Base;

            public static class Punctuation
            {
                public const string Exclamation = "!";
            }
            """);
        await DotnetAsync("pack", @base, "-o", Feed, "-p:Version=1.0.0");

        var greetings = Write($"packages-src/Greetings/{GreetingsId}.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <GenerateDocumentationFile>true</GenerateDocumentationFile>
                <DebugType>embedded</DebugType>
                <EmbedAllSources>true</EmbedAllSources>
                <Authors>Roslynaut</Authors>
                <Description>Greets people, for Roslynaut's end-to-end tests.</Description>
                <PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{{BaseId}}" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);
        foreach (var (version, members) in new[] { ("1.0.0", GreetingsV1), ("2.0.0", GreetingsV2), ("3.0.0-beta", GreetingsV2) })
        {
            Write("packages-src/Greetings/Greeter.cs", GreeterSource(members));
            await DotnetAsync("pack", greetings, "-o", Feed, $"-p:Version={version}");
        }
    }

    /// <summary>Greeter.cs of a version of Greetings, as its PDB embeds it.</summary>
    public static string GreeterSource(string members) => $$"""
        using Roslynaut.E2E.Base;

        namespace Roslynaut.E2E.Greetings;

        /// <summary>Greets people.</summary>
        public static class Greeter
        {
        {{members}}
        }
        """.ReplaceLineEndings("\n");

    public const string GreetingsV1 = """
            /// <summary>Says hello to someone.</summary>
            /// <param name="name">Who to greet.</param>
            public static string Hello(string name) => "Hello, " + name + Punctuation.Exclamation;

            /// <summary>Says goodbye to someone.</summary>
            /// <param name="name">Who to say goodbye to.</param>
            public static string Bye(string name) => "Bye, " + name + Punctuation.Exclamation;
        """;

    private const string GreetingsV2 = """
            /// <summary>Says hello to someone.</summary>
            /// <param name="name">Who to greet.</param>
            [System.Obsolete("Use Hi.")]
            public static string Hello(string name) => "Hello, " + name + Punctuation.Exclamation;

            /// <summary>Says hi to someone.</summary>
            /// <param name="name">Who to greet.</param>
            public static string Hi(string name) => "Hi, " + name + Punctuation.Exclamation;
        """;

    /// <summary>
    /// Declarations whose names code writes otherwise than metadata does, for tests to copy into a project of their own: a generic type, an
    /// explicit interface implementation, a finalizer, a record with an operator and a conversion (whose body declares a tuple), and a C# 14
    /// extension block beside an extension method.
    /// </summary>
    public const string Kinds = """
        using System;

        namespace Edits;

        public class Box<T> : IDisposable where T : class
        {
            public T? Item { get; set; }

            public TResult Map<TResult>(Func<T?, TResult> map) => map(Item);

            void IDisposable.Dispose() { }

            ~Box() { }
        }

        public record Money(decimal Amount)
        {
            public static Money operator +(Money a, Money b) => new(a.Amount + b.Amount);

            public static implicit operator (decimal, string)(Money money) => (money.Amount, "EUR");
        }

        public static class Text
        {
            public static int Twice(this int value) => value * 2;

            extension(string text)
            {
                public bool IsBlank => string.IsNullOrWhiteSpace(text);

                public string Shout() => text.ToUpperInvariant() + "!";
            }
        }

        public static class Use
        {
            public static string Run()
            {
                using var box = new Box<string> { Item = "a" };
                (decimal, string) pair = new Money(1) + new Money(2);
                return "x".IsBlank ? box.Map(item => item?.Length ?? 2.Twice()).ToString() : "x".Shout() + pair.Item2;
            }
        }
        """;

    private static readonly (string Path, string Text)[] Files =
    [
        ("Shapes.slnx", """
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/Shapes/Shapes.csproj" />
                <Project Path="src/Shapes.App/Shapes.App.csproj" />
              </Folder>
              <Folder Name="/tests/">
                <Project Path="tests/Shapes.Tests/Shapes.Tests.csproj" />
              </Folder>
            </Solution>
            """),
        ("src/Shapes/Shapes.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <GenerateDocumentationFile>true</GenerateDocumentationFile>
                <NoWarn>$(NoWarn);CS1591</NoWarn>
              </PropertyGroup>
            </Project>
            """),
        ("src/Shapes/IShape.cs", """
            namespace Shapes;

            /// <summary>A two-dimensional shape.</summary>
            public interface IShape
            {
                /// <summary>The name to show for the shape.</summary>
                string Name { get; }

                /// <summary>Computes the area of the shape.</summary>
                /// <returns>The area, in square units.</returns>
                double Area();
            }
            """),
        ("src/Shapes/Shape.cs", """
            namespace Shapes;

            /// <summary>The base of all shapes.</summary>
            public abstract class Shape : IShape
            {
                public virtual string Name => GetType().Name;

                public abstract double Area();

                public override string ToString() => $"{Name} with area {Area():F2}";
            }
            """),
        ("src/Shapes/Circle.cs", """
            using System;

            namespace Shapes;

            /// <summary>A circle around the origin.</summary>
            public sealed class Circle(double radius) : Shape
            {
                public double Radius { get; } = radius;

                public override double Area() => Math.PI * Radius * Radius;
            }
            """),
        ("src/Shapes/Rectangle.cs", """
            namespace Shapes;

            public class Rectangle(double width, double height) : Shape
            {
                public double Width { get; } = width;

                public double Height { get; } = height;

                public override double Area() => Width * Height;

                /// <summary>Scales both sides by one factor.</summary>
                /// <param name="factor">What to multiply both sides by.</param>
                public Rectangle Scale(double factor) => Scale(factor, factor);

                /// <summary>Scales each side by its own factor.</summary>
                /// <param name="x">What to multiply the width by.</param>
                /// <param name="y">What to multiply the height by.</param>
                public Rectangle Scale(double x, double y) => new(Width * x, Height * y);
            }
            """),
        ("src/Shapes/Square.cs", """
            namespace Shapes;

            public sealed class Square(double side) : Rectangle(side, side)
            {
                public override string Name => "Square";
            }
            """),
        ("src/Shapes/ShapeMath.cs", """
            using System;
            using System.Collections.Generic;

            namespace Shapes;

            public static class ShapeMath
            {
                /// <summary>Adds up the areas of the shapes.</summary>
                public static double TotalArea(IEnumerable<IShape> shapes)
                {
                    var total = 0.0;
                    foreach (var shape in shapes)
                    {
                        var area = shape.Area();
                        if (area > 0)
                            total += area;
                    }

                    return total;
                }

                public static string Classify(IShape shape) => shape.Area() switch
                {
                    < 1 => "small",
                    < 100 => "medium",
                    _ => "large",
                };

                public static IShape Largest(IReadOnlyList<IShape> shapes)
                {
                    IShape? largest = null;
                    foreach (var shape in shapes)
                    {
                        if (largest is null || shape.Area() > largest.Area())
                            largest = shape;
                    }

                    return largest ?? throw new ArgumentException("There are no shapes.", nameof(shapes));
                }
            }
            """),
        ("src/Shapes/ShapeFactory.cs", """
            using System;
            using Shapes.Internal;

            namespace Shapes;

            public static class ShapeFactory
            {
                /// <summary>Creates a shape from a description such as "circle 2".</summary>
                public static Shape Parse(string description) =>
                    Registry.Create(description) ?? throw new FormatException($"Unknown shape: {description}");
            }
            """),
        ("src/Shapes/Internal/Registry.cs", """
            using System;
            using System.Collections.Generic;
            using System.Globalization;
            using System.Text.RegularExpressions;

            namespace Shapes.Internal;

            internal static partial class Registry
            {
                private static readonly Dictionary<string, Func<double, Shape>> Factories = new()
                {
                    ["circle"] = size => new Circle(size),
                    ["square"] = size => new Square(size),
                };

                public static Shape? Create(string description)
                {
                    var match = Description().Match(description);
                    if (!match.Success)
                        return null;

                    var size = double.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture);
                    return Factories.TryGetValue(match.Groups["kind"].Value, out var factory) ? factory(size) : null;
                }

                private static int Unused() => 42;

                [GeneratedRegex(@"^(?<kind>[a-z]+) (?<size>[0-9.]+)$")]
                private static partial Regex Description();
            }
            """),
        ("src/Shapes.App/Shapes.App.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <ShapesFlavor>plain</ShapesFlavor>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Shapes\Shapes.csproj" />
                <PackageReference Include="{{GreetingsId}}" Version="1.0.0" />
              </ItemGroup>
              <!-- Never writes its output, so it runs in every build. -->
              <Target Name="WriteBuildInfo" AfterTargets="Build" Inputs="$(MSBuildProjectFile)" Outputs="$(IntermediateOutputPath)buildinfo.txt">
                <PropertyGroup><ShapesFlavor>$(ShapesFlavor);built</ShapesFlavor></PropertyGroup>
                <Message Text="Flavor $(ShapesFlavor)" />
              </Target>
            </Project>
            """),
        ("src/Shapes.App/Program.cs", """
            using Roslynaut.E2E.Greetings;
            using Shapes;
            using Shapes.App;

            IShape[] shapes = [new Circle(1), new Rectangle(2, 3), new Square(4)];
            var total = ShapeMath.TotalArea(shapes);
            Console.WriteLine(Greeter.Hello("shapes"));
            Console.WriteLine(Greeter.Bye("shapes"));
            Console.WriteLine($"Total area: {Math.Round(total)}");
            if (args is ["wait"])
            {
                new Thread(() => Spinner.Spin(shapes)) { IsBackground = true }.Start();
                Console.WriteLine("Waiting");
                Console.ReadLine();
            }
            """),
        ("src/Shapes.App/Spinner.cs", """
            using Shapes;

            namespace Shapes.App;

            /// <summary>Keeps a CPU busy, without allocating, for the process tools to watch.</summary>
            internal static class Spinner
            {
                public static void Spin(IShape[] shapes)
                {
                    var sum = 0.0;
                    while (true)
                    {
                        foreach (var shape in shapes)
                            sum += shape.Area();
                    }
                }
            }
            """),
        ("tests/Shapes.Tests/Shapes.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.11.0" />
                <PackageReference Include="NUnit" Version="5.0.0" />
                <PackageReference Include="NUnit3TestAdapter" Version="6.3.1" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="..\..\src\Shapes\Shapes.csproj" />
              </ItemGroup>
            </Project>
            """),
        ("tests/Shapes.Tests/ShapeTests.cs", """
            using System;
            using NUnit.Framework;

            namespace Shapes.Tests;

            public class ShapeTests
            {
                [Test]
                public void Circle_Area_IsPiTimesRadiusSquared() => Assert.That(new Circle(2).Area(), Is.EqualTo(Math.PI * 4).Within(1e-9));

                [Test]
                public void Square_Area_IsSideSquared() => Assert.That(new Square(3).Area(), Is.EqualTo(9));

                [Test]
                public void TotalArea_AddsTheAreas()
                {
                    var total = ShapeMath.TotalArea([new Square(1), new Rectangle(2, 3)]);
                    Assert.That(total, Is.EqualTo(7));
                }

                // Fails on purpose: an area of 1 is medium.
                [Test]
                public void Classify_UnitSquare_IsSmall() => Assert.That(ShapeMath.Classify(new Square(1)), Is.EqualTo("small"));

                [Test]
                [Ignore("Not written yet")]
                public void Largest_PicksTheBiggest()
                {
                }
            }
            """),
        ("Problems/Problems.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """),
        ("Problems/Account.cs", """
            namespace Problems;

            public class Account
            {
                private int balance;

                public void Deposit(int amount)
                {
                    int unused;
                    balance += amount;
                }

                public string Describe() => balance;
            }
            """),
        ("Targets/Targets.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net10.0;netstandard2.0</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """),
        ("Targets/Platform.cs", """
            namespace Targets;

            public static class Platform
            {
            #if NETSTANDARD2_0
                public static string Name => "standard";
            #else
                public static string Name => "core";
            #endif
            }
            """),
    ];
}
