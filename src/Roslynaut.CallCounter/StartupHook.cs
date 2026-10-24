using System.Diagnostics;
using System.Globalization;
using System.Runtime.Loader;
using System.Text;

/// <summary>
/// Counts calls of the methods that process_calls instrumented, in a program it starts with DOTNET_STARTUP_HOOKS naming this assembly. The
/// deps.json it starts the program with leaves out the instrumented assemblies, so the runtime asks for them, and this loads the instrumented
/// copies from ROSLYNAUT_CALLS_DIRECTORY. The counts of the ROSLYNAUT_CALLS_METHODS methods go to the file ROSLYNAUT_CALLS_FILE.
/// </summary>
internal static class StartupHook
{
    public static void Initialize()
    {
        var directory = Environment.GetEnvironmentVariable("ROSLYNAUT_CALLS_DIRECTORY")!;
        AssemblyLoadContext.Default.Resolving += (context, name) =>
            Path.Combine(directory, $"{name.Name}.dll") is var path && File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        Roslynaut.CallCounter.Counter.Start(
            Environment.GetEnvironmentVariable("ROSLYNAUT_CALLS_FILE")!, int.Parse(Environment.GetEnvironmentVariable("ROSLYNAUT_CALLS_METHODS")!, CultureInfo.InvariantCulture));
    }
}

namespace Roslynaut.CallCounter
{
    /// <summary>The counts, per thread, which every instrumented method adds to first thing; each thread counts in its own array, without locking.</summary>
    public static class Counter
    {
        private static readonly List<(Thread Thread, long[] Counts)> Threads = [];
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static string s_file = "";
        private static int s_methods;

        [ThreadStatic]
        private static long[]? t_counts;

        public static void Hit(int method) => (t_counts ?? Register())[method]++;

        private static long[] Register()
        {
            var counts = t_counts = new long[s_methods];
            lock (Threads)
                Threads.Add((Thread.CurrentThread, counts));
            return counts;
        }

        internal static void Start(string file, int methods)
        {
            (s_file, s_methods) = (file, methods);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Write();
            // ponytail: the counts reach the file every 100 ms, so a program that is stopped loses its last 100 ms; signal it to write first if that matters.
            new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(100);
                    Write();
                }
            }) { IsBackground = true, Name = "Roslynaut call counter" }.Start();
        }

        /// <summary>Lines: the seconds since the program started, then per thread its managed id, its name and its counts, separated by tabs.</summary>
        private static void Write()
        {
            lock (Threads)
            {
                var text = new StringBuilder(Clock.Elapsed.TotalSeconds.ToString(CultureInfo.InvariantCulture)).Append('\n');
                foreach (var (thread, counts) in Threads)
                    text.Append(CultureInfo.InvariantCulture, $"{thread.ManagedThreadId}\t{thread.Name?.Replace('\t', ' ').Replace('\n', ' ')}\t{string.Join(' ', counts)}\n");

                // Whole, so the server never reads half of it; it may be reading the last one meanwhile, which then stays until the next time.
                try
                {
                    File.WriteAllText(s_file + ".tmp", text.ToString());
                    File.Move(s_file + ".tmp", s_file, overwrite: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
