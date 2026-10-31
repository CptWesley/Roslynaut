using System.Net;
using System.Net.Sockets;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>
/// process_cpu and process_events with launch: a web app that serves and sends requests, logs, throws and catches, waits for a lock, calls
/// GC.Collect and spins, for a few seconds from its start; and a test project whose test spins.
/// </summary>
[NonParallelizable]
public class LaunchTracingTests
{
    private readonly WorkspaceService workspace = new();
    private DirectoryInfo directory = null!;
    private string app = null!;
    private string tests = null!;

    [OneTimeSetUp]
    public void CreateProjects()
    {
        directory = Directory.CreateTempSubdirectory("roslynaut-launch-");
        void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(directory.FullName, path))!);
            File.WriteAllText(Path.Combine(directory.FullName, path), text);
        }

        app = Path.Combine(directory.FullName, "Shop", "Shop.csproj");
        Write("Shop/Shop.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
            </Project>
            """);
        Write("Shop/Program.cs", """
            var (url, seconds) = (args[0], int.Parse(args[1]));
            var app = WebApplication.Create();
            app.Urls.Add(url);
            app.MapGet("/orders/{id}", (int id, ILogger<Program> logger) => { logger.LogInformation("Order {Id} looked up", id); return id; });
            await app.StartAsync();

            using var http = new HttpClient();
            var gate = new object();
            var stop = DateTime.UtcNow.AddSeconds(seconds);
            new Thread(() => { while (DateTime.UtcNow < stop) lock (gate) Thread.Sleep(20); }) { IsBackground = true }.Start();
            while (DateTime.UtcNow < stop)
            {
                await http.GetStringAsync($"{url}/orders/7");
                try { Busy.Fail(); } catch (InvalidOperationException) { }
                lock (gate) { }
                GC.Collect();
                Busy.Spin();
            }

            await app.StopAsync();

            static class Busy
            {
                public static void Fail() => throw new InvalidOperationException("No such order");
                // Mostly managed code: on Linux, TickCount64 waits in native code for each sample, and the stacks stop there.
                public static void Spin() { var end = Environment.TickCount64 + 20; while (Environment.TickCount64 < end) for (var i = 0; i < 100_000; i++) Sink += i % 7; }
                public static long Sink;
            }
            """);

        tests = Path.Combine(directory.FullName, "Spin.Tests", "Spin.Tests.csproj");
        Write("Spin.Tests/Spin.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.11.0" />
                <PackageReference Include="NUnit" Version="5.0.0" />
                <PackageReference Include="NUnit3TestAdapter" Version="6.3.1" />
              </ItemGroup>
            </Project>
            """);
        Write("Spin.Tests/SpinTests.cs", """
            using NUnit.Framework;
            namespace Spin;
            public class SpinTests
            {
                [Test] public void Spins() => Work();
                [Test] public void Other() { }
                private static void Work() { var end = System.Environment.TickCount64 + 1500; while (System.Environment.TickCount64 < end) { } }
            }
            """);
    }

    [OneTimeTearDown]
    public void DeleteProjects() => directory.Delete(recursive: true);

    private TraceTarget Shop()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new(null, new(app, [$"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}", "4"], null, workspace));
    }

    private static readonly TimeSpan AtMost = TimeSpan.FromSeconds(60);

    [Test]
    public async Task Cpu_OfALaunchedProgram_CoversItUntilItExits()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var profile = ProcessTracing.Analyze("1", await ProcessTracing.SampleAsync(Shop(), AtMost, CancellationToken.None), null, new(null, null, null, null), top: 30);

        watch.Elapsed.Should().BeLessThan(AtMost);
        profile.Methods.Should().Contain(method => method.Method.Contains("Busy.Spin"));
    }

    [Test]
    public async Task Cpu_OfALaunchedTestProject_IsOfTheTestHost()
    {
        var samples = await ProcessTracing.SampleAsync(new TraceTarget(null, new(tests, [], "FullyQualifiedName~Spin.SpinTests.Spins", workspace)), AtMost, CancellationToken.None);

        // Work spins on Environment.TickCount64, which runs itself the most.
        var profile = ProcessTracing.Analyze("1", samples, null, new(null, null, new("SpinTests\\.Work"), null), top: 30);
        profile.Callees.Should().ContainSingle().Which.Total.Should().BeGreaterThan(50);
    }

    [Test]
    public async Task Exceptions_ByTypeAndThrower_WithWhereCaught()
    {
        var events = await ProcessTracing.ExceptionsAsync(Shop(), AtMost, new("InvalidOperation"), top: 10, maxFrames: 10, CancellationToken.None);

        var thrown = events.Exceptions.Should().ContainSingle().Subject;
        (thrown.Type, thrown.Message).Should().Be(("System.InvalidOperationException", "No such order"));
        thrown.ThrownIn.Should().Contain("Busy.Fail");
        thrown.Count.Should().BePositive();
        thrown.CaughtIn.Should().NotContain("(not caught)");
    }

    [Test]
    public async Task Logs_ComeThroughTheEventSourceLogger()
    {
        var events = await ProcessTracing.LogsAsync(Shop(), AtMost, new("^Program$"), level: 2, top: 10, CancellationToken.None);

        events.Logs.Should().NotBeEmpty().And.OnlyContain(log => log.Category == "Program" && log.Level == "Information" && log.Message == "Order 7 looked up");
    }

    [Test]
    public async Task Requests_ServedByRoute_AndSentByHost()
    {
        var events = await ProcessTracing.RequestsAsync(Shop(), AtMost, null, top: 10, CancellationToken.None);

        events.Requests.Should().Contain(request => request.Kind == "Served" && request.Target == "/orders/{id}" && request.Method == "GET" && request.Status == "200" && request.Count > 0);
        events.Requests.Should().Contain(request => request.Kind == "Sent" && request.Target.StartsWith("127.0.0.1:") && request.P95Ms > 0);
    }

    [Test]
    public async Task Contention_AndCollections_HaveTheirStacks()
    {
        var contention = (await ProcessTracing.ContentionAsync(Shop(), AtMost, null, top: 10, maxFrames: 10, CancellationToken.None)).Contention!;
        contention.Waits.Should().BePositive();
        contention.Stacks.Should().NotBeEmpty();

        var collections = (await ProcessTracing.CollectionsAsync(Shop(), AtMost, top: 5, maxFrames: 10, CancellationToken.None)).Collections!;
        collections.Count.Should().BePositive();
        collections.Longest.Should().NotBeEmpty();
        collections.Induced.Should().Contain(stack => stack.Stack.Any(frame => frame.Contains("GC.Collect")));
    }
}
