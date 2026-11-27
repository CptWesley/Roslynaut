using System.Collections.Frozen;

namespace Roslynaut;

/// <summary>
/// The caveman text of the tools' and their parameters' descriptions, by their full text, which --tool-descriptions and
/// --field-descriptions caveman serve: without articles and filler, keeping identifiers, constraints and the keywords agents search tools by.
/// Keyed by text, so that a description that tools share has one entry. McpServerServiceTests fails on a description without one, and on
/// an entry no description has any more: a changed description needs its caveman text changed with it.
/// </summary>
internal static class CavemanDescriptions
{
    internal static readonly FrozenDictionary<string, string> Text = new Dictionary<string, string>
    {
        // project_info
        ["Returns MSBuild information per project: target framework, key properties, project and package references; and for a project that did not load, the MSBuild errors that explain why, such as a restore that did not run."] =
            "MSBuild info/project: target framework, key props, project/package refs; load failed->MSBuild errors why (e.g. no restore)",
        ["Path to a .csproj, .sln or .slnx file."] =
            ".csproj|.sln|.slnx path",

        // workspace_symbols
        ["Searches type and member declarations by name, matching substrings and camel-case humps like an IDE's 'Go to symbol'. Paged."] =
            "Find type/member decls by name: substring+camel-case humps, like IDE 'Go to symbol'. Paged",
        ["Name or pattern to search for, e.g. 'Workspace' or 'GDA' for 'GetDocumentAsync'. May be qualified with namespaces and containing types, e.g. 'Tools.WorkspaceService.GetDocumentAsync'."] =
            "Name/pattern, e.g. 'GDA'->'GetDocumentAsync'; qualify opt: 'Tools.WorkspaceService.GetDocumentAsync'",
        ["0-based page number; hasMore in the result tells whether a next page exists."] =
            "0-based page; hasMore=next exists",
        ["Number of results per page."] =
            "Results/page",

        // document_symbols
        ["Outlines a C# file: its types and their members (methods, properties, fields, events) with signatures and positions. Paged."] =
            "Outline C# file: types+members (methods/props/fields/events) w/ signatures+positions. Paged",
        ["Path to a C# source file in the project."] =
            "C# source file in project",
        ["Optional nesting depth: 0 for the top-level types only, 1 to add their members and nested types, and so on; without it, everything."] =
            "Opt nesting depth: 0=top-level types, 1=+members/nested, ...; omit=all",

        // list_symbols
        ["Lists the symbols declared in a symbol: a namespace's namespaces and types, a type's members, a method's or indexer's parameters and locals. For a variable, field or other property it lists the members of its type. Paged."] =
            "Symbols in symbol: namespace->namespaces+types; type->members; method/indexer->params+locals; var/field/prop->its type's members. Paged",
        ["Symbol path: a dotted name, optionally partly qualified, e.g. 'OrderService', 'OrderService.Submit(Order)' for one overload, 'OrderService.Submit.order' for a parameter or local, 'OrderService..ctor', 'Type.this[int]'. A result's Container + '.' + Name is a path."] =
            "Symbol path: dotted, partial ok; 'OrderService.Submit(Order)' overload, 'OrderService.Submit.order' param/local, 'Type..ctor', 'Type.this[int]'; result Container+'.'+Name=path",

        // find_symbols
        ["Searches symbols (namespaces, types, members, parameters, locals) by name and structure (attribute, base type, type, accessibility, modifiers) in the whole solution or within a scope. Outer symbols come first. Paged."] =
            "Find symbols (namespaces..locals) by name+structure (attribute, base type, type, accessibility, modifiers) in solution/scope; outer first. Paged",
        ["Case-insensitive .NET regular expression matched against symbol names, e.g. 'cache', '^Get.*Async$' or 'path|file'. A query with a dot also matches full names that end in the symbol's name, e.g. 'Tools.ResultCache' or 'MyNamespace.Outer.Nested'. Omit to return everything in scope."] =
            "Case-insensitive .NET regex on names, e.g. '^Get.*Async$'; w/ dot matches full-name suffix: 'Tools.ResultCache'. Omit=all",
        ["Optional path of a C# file, or a symbol path to search within. Symbol path: a dotted name, optionally partly qualified, e.g. 'OrderService', 'OrderService.Submit(Order)' for one overload, 'OrderService.Submit.order' for a parameter or local, 'OrderService..ctor', 'Type.this[int]'. A result's Container + '.' + Name is a path."] =
            "Opt scope: C# file|symbol path. Symbol path: dotted, partial ok; 'OrderService.Submit(Order)' overload, 'OrderService.Submit.order' param/local, 'Type..ctor', 'Type.this[int]'; result Container+'.'+Name=path",
        ["Kind of symbols to return. Variable means locals and parameters."] =
            "Symbol kind. Variable=locals+params",
        ["Optional regex for the name of an attribute the symbol has, with or without 'Attribute', e.g. 'Obsolete' or '^(Test|Fact)$'."] =
            "Opt regex: attribute name, 'Attribute' suffix opt, e.g. '^(Test|Fact)$'",
        ["Optional regex for a base type or interface of the type, e.g. 'IDisposable' or 'ControllerBase'."] =
            "Opt regex: base type/interface, e.g. 'IDisposable'",
        ["Optional regex for the type of a variable, field, property or event, or the return type of a method, e.g. '^Task' or 'List<int>'."] =
            "Opt regex: type of var/field/prop/event or method return, e.g. '^Task'",
        ["Optional accessibility: Public, Internal, Protected, Private, ProtectedOrInternal or ProtectedAndInternal."] =
            "Opt accessibility: Public|Internal|Protected|Private|ProtectedOrInternal|ProtectedAndInternal",
        ["Optional modifiers that must all apply: static, abstract, virtual, override, sealed, extern, async, readonly, const, extension or generic."] =
            "Opt modifiers, all must apply: static|abstract|virtual|override|sealed|extern|async|readonly|const|extension|generic",

        // definition
        ["Resolves a symbol: its signature, documentation (with <inheritdoc/> expanded, and inherited by an undocumented override or implementation) and declaration locations (go to definition + hover). For a symbol from a referenced assembly, AssemblyPath is the file to pass to assembly_il, assembly_asm or decompile; and if its PDB leads to source, Locations point into local copies of the files that Sources describe."] =
            "Resolve symbol: signature, docs (inheritdoc expanded, undocumented overrides inherit), decl locations (go to definition+hover). Referenced assembly: AssemblyPath for assembly_il/assembly_asm/decompile; PDB source->Locations in local copies (Sources)",
        ["The symbol, by path; or leave this out and give filePath, line and column. Symbol path: a dotted name, optionally partly qualified, e.g. 'OrderService', 'OrderService.Submit(Order)' for one overload, 'OrderService.Submit.order' for a parameter or local, 'OrderService..ctor', 'Type.this[int]'. A result's Container + '.' + Name is a path."] =
            "Symbol path, or omit + filePath+line+column. Symbol path: dotted, partial ok; 'OrderService.Submit(Order)' overload, 'OrderService.Submit.order' param/local, 'Type..ctor', 'Type.this[int]'; result Container+'.'+Name=path",
        ["With line and column, instead of a symbol path: the C# file that contains the symbol."] =
            "C# file of symbol, w/ line+column instead of symbol path",
        ["1-based line number."] =
            "1-based line",
        ["1-based column number of the symbol."] =
            "1-based column of symbol",
        ["Resolve the symbol's type instead (go to type definition): the type of a variable, parameter, field, property or event, or the return type of a method, or for a constructor its type; for an array its element type."] =
            "Resolve type instead (go to type definition): of var/param/field/prop/event, method return; ctor->its type; array->element",

        // references
        ["Finds all references to a symbol across the project or solution, with the member that contains each reference and whether it reads, writes or calls the symbol. Paged."] =
            "All references to symbol in project/solution, w/ containing member + read/write/call. Paged",

        // symbol_source
        ["Returns the source code of a symbol's declaration, such as a whole method, property or type; one entry per declaration, so a partial type has several. Each Source starts at the beginning of the line given by its Location. For a symbol from a referenced assembly, the source comes from the files its PDB lists (see assembly_sources), and Document tells where from and whether it is verified. That source lacks what the compiler synthesizes or IL weavers add, which decompile shows."] =
            "Symbol declaration source (whole method/prop/type), 1 per decl (partial->several); starts at line start of Location. Referenced assembly: from PDB files (see assembly_sources), Document=origin+verified; lacks compiler-synthesized/IL-woven code->decompile",
        ["For a symbol declared in source: also the last this many commits that changed its lines, with git log -L, each with its author, date, message and the diff of those lines: why the code is as it is, or which commit brought in a change."] =
            "Source symbol: + last N commits touching its lines (git log -L): author, date, message, diff -> why code is so / which commit changed it",

        // implementations
        ["Finds what implements or overrides a symbol: the types implementing an interface, the members implementing an interface member, and the overrides of an abstract or virtual member. Paged."] =
            "What implements/overrides symbol: interface->types; interface member->members; abstract/virtual->overrides. Paged",

        // type_hierarchy
        ["Lists the supertypes (base types, then all implemented interfaces) or the subtypes (all derived types, and for an interface its derived interfaces and implementing types) of a type. For a variable, field or property, of its type; for a constructor, of the type it constructs. Paged."] =
            "Supertypes (base types, then all interfaces) or subtypes (all derived; interface->derived interfaces+implementing types) of type. Var/field/prop->its type; ctor->its type. Paged",
        ["Supertypes or Subtypes."] =
            "Supertypes|Subtypes",

        // callers
        ["Finds the members that call or use a symbol (incoming calls), each with the locations of its calls. Calls through an interface or base member are included. Paged."] =
            "Members calling/using symbol (incoming calls) w/ call locations; incl via interface/base member. Paged",

        // callees
        ["Finds what a member uses (outgoing calls): the methods, constructors, properties, events, method groups and user-defined operators it refers to, each with the locations it refers to them at. Paged."] =
            "What member uses (outgoing calls): methods, ctors, props, events, method groups, user-defined operators, w/ locations. Paged",

        // expression_info
        ["Describes the expression at a position and up to 4 expressions around it, innermost first: its type, the type it converts to and how, its constant value, its null state and the symbol it binds to; when binding fails, the candidates and why they failed."] =
            "Expression at position + up to 4 around, innermost first: type, conversion (to+how), constant, null state, bound symbol; bind fail->candidates+why",
        ["1-based column within the expression."] =
            "1-based column in expression",

        // data_flow
        ["Analyzes the statements between two lines, which must be in one block, as for extracting a method: the variables declared, flowing in and out, read and written inside and outside, and captured by lambdas; and the exits (return, break, continue, goto) and whether the end can be reached."] =
            "Data flow of statements between 2 lines (same block), as extract method: vars declared/in/out/read/written in+outside/lambda-captured; exits (return/break/continue/goto); end reachable?",
        ["1-based first line."] =
            "1-based first line",
        ["1-based last line."] =
            "1-based last line",

        // unused_symbols
        ["Finds types and members declared in source that nothing references, not even the code under #if that the load leaves out; the members of an unused type are left out, and so is generated code. Public ones are left out unless asked for, as they can be used from outside. So are overrides, interface implementations, constructors, entry points and their types, and symbols with attributes, as frameworks, serializers or reflection may use those. Paged."] =
            "Unused source types/members: no refs, even in #if code load skips. Skips: unused type's members, generated, public (unless asked), overrides, interface impls, ctors, entry points+their types, attributed (framework/serializer/reflection). Paged",
        ["Optional path of a C# file, or a symbol path, to search within. Symbol path: a dotted name, optionally partly qualified, e.g. 'OrderService', 'OrderService.Submit(Order)' for one overload, 'OrderService.Submit.order' for a parameter or local, 'OrderService..ctor', 'Type.this[int]'. A result's Container + '.' + Name is a path."] =
            "Opt scope: C# file|symbol path. Symbol path: dotted, partial ok; 'OrderService.Submit(Order)' overload, 'OrderService.Submit.order' param/local, 'Type..ctor', 'Type.this[int]'; result Container+'.'+Name=path",
        ["Include symbols that are visible outside the assembly too."] =
            "Incl symbols visible outside assembly",

        // generated_sources
        ["Lists the files that source generators add to the projects, such as for System.Text.Json, regular expressions or logging; with a name, returns the content of the matching files. They are part of the compilation but not on disk. Paged."] =
            "Source generator files in projects (e.g. System.Text.Json, regex, logging); w/ name->content. In compilation, not on disk. Paged",
        ["Optional hint name, or part of it, of the files to return the content of, e.g. 'RegexGenerator' or 'MyContext.g.cs'."] =
            "Opt hint name (or part) of files to show, e.g. 'MyContext.g.cs'",

        // document_highlight
        ["Finds where the symbol at a position occurs in that same file, in order: its declarations, reads, writes and calls."] =
            "Occurrences of symbol at position in its file, in order: decls, reads, writes, calls",

        // signature_help
        ["At a position between the parentheses (or brackets) of a call, constructor or indexer: the overloads it could call, with their documentation, which one the arguments pick, and which parameter the argument at the position goes to."] =
            "Inside parens/brackets of call/ctor/indexer: overloads w/ docs, one args pick, param for arg at position",
        ["1-based column within the argument list."] =
            "1-based column in argument list",

        // completion
        ["Lists what can be written at a position, as an IDE's completion list does: the members after a dot, or the locals, parameters, members, types and keywords in scope there. A filter keeps the list short. Paged."] =
            "Completion at position, as IDE: members after dot, or in-scope locals/params/members/types/keywords. Filter shortens. Paged",
        ["1-based column where the text would go, such as just after a dot."] =
            "1-based column where text goes, e.g. after dot",
        ["Optional: only items whose name starts with this, ignoring case."] =
            "Opt: only names starting w/ this, case-insensitive",

        // inlay_hints
        ["Returns lines of a file with the hints an IDE shows inline, as /*...*/ comments: the type inferred for var and for lambda parameters, and the parameter names of literal and new arguments. Each line starts with its number."] =
            "File lines w/ IDE inlay hints as /*...*/: var + lambda param types, param names for literal/new args. Lines numbered",

        // syntax_tree
        ["Dumps the syntax tree of some lines of a file, from the smallest node that covers them: each node with its kind, line and text, indented by depth. With operations, dumps the semantic operation tree instead, with types, constants and the symbols operations refer to. For writing analyzers or generators, or understanding how code binds. At most 400 nodes."] =
            "Dump syntax tree of lines from smallest covering node: kind, line, text, indented. W/ operations->semantic operation tree w/ types, constants, symbols. For analyzers/generators, binding. Max 400 nodes",
        ["Dump operations instead of syntax."] =
            "Dump operations, not syntax",

        // code_metrics
        ["Measures code, most complex first. Members: the cyclomatic complexity, lines, parameters and deepest nesting of each method, constructor, operator, or property or event with code. Types: their member count, lines, total and highest member complexity, and class coupling: how many other types they use. In the whole solution, a file or a symbol. Generated code, such as source generators' output and files marked auto-generated, is left out unless asked for, and so are the generated parts of partial types. Paged."] =
            "Code metrics, most complex first. Members: cyclomatic complexity, lines, params, max nesting per method/ctor/operator/prop|event w/ body. Types: member count, lines, total+max member complexity, class coupling (# types used). Solution/file/symbol. Generated code (source generators, auto-generated) + generated partial parts excluded unless asked. Paged",
        ["Optional path of a C# file, or a symbol path, to measure within. Symbol path: a dotted name, optionally partly qualified, e.g. 'OrderService', 'OrderService.Submit(Order)' for one overload, 'OrderService.Submit.order' for a parameter or local, 'OrderService..ctor', 'Type.this[int]'. A result's Container + '.' + Name is a path."] =
            "Opt scope: C# file|symbol path. Symbol path: dotted, partial ok; 'OrderService.Submit(Order)' overload, 'OrderService.Submit.order' param/local, 'Type..ctor', 'Type.this[int]'; result Container+'.'+Name=path",
        ["Members or Types."] =
            "Members|Types",
        ["Measure generated code too, and count the generated parts of partial types."] =
            "Incl generated code + generated parts of partial types",

        // search_pattern
        ["Searches the text of the solution's C# files, including generated ones that are not on disk, with a regular expression; within a file, a project or a symbol's declaration if asked. Returns each match, once per line it starts on, with its lines and context lines, numbered. Paged."] =
            "Regex text search of solution C# files incl generated (not on disk); opt file/project/symbol decl. Each match once per start line, numbered lines+context. Paged",
        ["A .NET regular expression; ^ and $ match at line ends, and it can span lines, e.g. 'async void' or 'catch\\s*\\{\\s*\\}'."] =
            ".NET regex; ^ $ at line ends; may span lines, e.g. 'catch\\s*\\{\\s*\\}'",
        ["Optional scope: a C# file, a project name, or a symbol path to search only its declaration; without it, every file of the solution, generated ones included. Symbol path: a dotted name, optionally partly qualified, e.g. 'OrderService', 'OrderService.Submit(Order)' for one overload, 'OrderService.Submit.order' for a parameter or local, 'OrderService..ctor', 'Type.this[int]'. A result's Container + '.' + Name is a path."] =
            "Opt scope: C# file, project name or symbol path (decl only); omit=all files incl generated. Symbol path: dotted, partial ok; 'OrderService.Submit(Order)' overload, 'OrderService.Submit.order' param/local, 'Type..ctor', 'Type.this[int]'; result Container+'.'+Name=path",
        ["How many lines of context to show before and after each match."] =
            "Context lines before+after match",
        ["Match case."] =
            "Match case",

        // find_operations
        ["Finds code by what it does rather than by name: the operations of a kind, such as Invocation, ObjectCreation, Throw, Await, Lock, Using, Conversion, Literal or Coalesce, and optionally only those whose target matches a regex: the method called, the type created or thrown, the type converted to, or else the operation's type. Compiler-generated ones, such as implicit conversions, count too. Paged."] =
            "Find code by what it does: operations of kind (Invocation, ObjectCreation, Throw, Await, Lock, Using, Conversion, Literal, Coalesce...), opt target regex: method called, type created/thrown/converted to, else operation type. Incl compiler-generated (implicit conversions). Paged",
        ["The kind of operation, a name of Roslyn's OperationKind, e.g. 'Invocation' or 'ObjectCreation'."] =
            "Roslyn OperationKind name, e.g. 'Invocation'",
        ["Optional case-insensitive regex for the target, e.g. 'HttpClient' or 'Task\\.Result'."] =
            "Opt case-insensitive target regex, e.g. 'Task\\.Result'",

        // evaluate
        ["Runs C# script code, such as an expression or a few statements, in a separate process that references the projects' current code and their dependencies; returns what it wrote to the console and the value of its last expression."] =
            "Run C# script (expression/statements) in separate process w/ projects' current code+deps; returns console output + last expression value",
        ["C# script code. A last expression without a semicolon is the value, e.g. 'new MyApp.Parser().Parse(\"1+2\")'. System, System.Collections.Generic, System.IO, System.Linq, System.Text and System.Threading.Tasks are imported."] =
            "Script; last expression w/o ';' = value, e.g. 'new MyApp.Parser().Parse(\"1+2\")'. Imports System, System.Collections.Generic, System.IO, System.Linq, System.Text, System.Threading.Tasks",
        ["How long it may run before it is stopped."] =
            "Max run time; then stopped",

        // dotnet_processes
        ["Lists the running .NET processes, to inspect with process_threads or process_heap: their id, name, start time and executable. With processId (or dumpPath), only that one, with how it was started and what it loaded: its command line, environment variables, runtime version and assemblies with their versions and paths, for a FileLoadException or a version conflict, or to check that it runs the build it should."] =
            "List running .NET processes: id, name, start, exe. processId|dumpPath -> that one + cmd line, env vars, runtime ver, assemblies (ver+path): FileLoadException, version conflict, right build?",
        ["The id of one process to describe in detail, e.g. from this list."] =
            "Process id to detail",
        ["Or else a dump file to describe: its runtime and assemblies."] =
            "Or dump file: runtime+assemblies",
        ["Optional regex for the paths of the assemblies to list, e.g. 'Newtonsoft|MyApp'."] =
            "Opt regex on assembly paths, e.g. 'Newtonsoft|MyApp'",

        // process_threads
        ["Snapshots a running .NET process and returns its managed threads, each with its call stack, the exception it is handling, if any, and the locks it holds that other threads wait for: for hangs, deadlocks and errors in progress; an async method that waits has no thread, so process_async shows those. Frames give their source file and line where a PDB is at hand, also one that assembly_sources or symbol_source downloaded earlier. The process pauses only while the snapshot is taken. Reads a dump file instead, such as one of a crash or a hang."] =
            "Snapshot running .NET process|dump -> managed threads: stack, exception in flight, locks others wait on. Hangs, deadlocks, errors. Awaiting async -> process_async. Frames file+line if PDB. Brief pause",
        ["The id of a running process, e.g. from dotnet_processes; or else pass dumpPath."] =
            "Running process id; or dumpPath",
        ["The path of a dump file to read instead of a running process, such as one from dotnet-dump collect or a crash."] =
            "Dump file instead (dotnet-dump collect, crash)",
        ["How many frames to return per thread, from the innermost."] =
            "Frames/thread, innermost first",
        ["Also find the System.Threading.Lock objects each thread holds, which takes a walk of the heap; Monitor locks (C# lock on an object) show without it."] =
            "Also System.Threading.Lock held per thread (heap walk); Monitor locks (C# lock) show w/o",

        // process_heap
        ["Snapshots a running .NET process and summarizes its managed heap: the object count and size, and the types that take the most memory with their object counts and sizes: for memory leaks and growth. With compareTo, the Snapshot of an earlier call, the types that grew the most since, with how much, which points at a leak. Walking a big heap takes a while. Reads a dump file instead, if it has the heap."] =
            "Snapshot running .NET process heap: object count+size, top types by memory. Memory leaks, growth. compareTo earlier Snapshot -> types grown most, by how much. Big heap slow. Or dump w/ heap",
        ["Optional regex for the type names to show, e.g. '^MyApp\\.' or 'Cache'."] =
            "Opt regex on type names, e.g. '^MyApp\\.'",
        ["How many types to return, largest first, or that grew most first with compareTo."] =
            "Types to return, largest (or most grown) first",
        ["The Snapshot of an earlier process_heap result to compare with."] =
            "Earlier process_heap Snapshot to compare",

        // process_objects
        ["Shows objects on the managed heap of a running .NET process or a dump, with their field values: the object at an address, or else the first objects of a type, such as the ones process_heap counts. A field that references an object shows its address, to follow with another call. Also says which thread holds the object's lock, if one does."] =
            "Heap objects of running .NET process|dump w/ field values: at address, or first of type. Ref fields give address -> follow. + thread holding its lock",
        ["The address of an object, such as 0x1f2e3d4c5b6a from a field or a root chain."] =
            "Object address, e.g. 0x1f2e3d4c5b6a",
        ["Or else a regex for the type names of the objects to show, e.g. '^MyApp\\.OrderCache$'."] =
            "Or regex on type names, e.g. '^MyApp\\.OrderCache$'",
        ["How many objects of the type to show."] =
            "Max objects of type",

        // process_roots
        ["Explains why an object in a running .NET process or a dump stays alive, as gcroot does: the chains of references from roots, such as locals, static fields and handles, to the object. For memory leaks, after process_heap and process_objects. Slow on big heaps. Finding locals needs the code the JIT compiled, which only a full dump has: in other dumps, chains from locals can be missing."] =
            "Why object stays alive (process|dump), like gcroot: ref chains roots (locals, statics, handles) -> object. Memory leaks. Slow on big heaps. Local roots need full dump",
        ["The address of the object, e.g. from process_objects."] =
            "Object address, e.g. from process_objects",
        ["How many chains to return."] =
            "Max chains",

        // process_cpu
        ["Profiles where a running .NET process spends its time, as dotnet-trace's CPU sampling does: samples managed thread stacks for a while and returns the threads by how busy they were, and the methods that run most, with their own share of the samples and their share including what they call. Filters pick the threads that run a method and leave out samples, such as of waits; root gives the call tree under a method, leaf the call paths into one. Each result has a Profile, to analyze the same samples again with other filters, or to compare a later profile with, as before and after a change. For slow code and high CPU; symbol_asm shows the code of a hot method. The process keeps running. With launch, it starts a program or a test project instead and profiles it from its start, for code that ends before it could be attached to, such as a slow test. With trace, it reads a .nettrace recorded earlier instead, such as by dotnet-trace collect on CI or another machine."] =
            "CPU profile of running .NET process (dotnet-trace sampling): busiest threads, hottest methods (self% + incl%). Filters: threads, drop samples (waits); root = call tree under, leaf = paths into. Result Profile -> refilter or compare before/after. Slow code, high CPU; symbol_asm for hot method. Process keeps running. launch: profile program|test project from start (e.g. slow test). trace: read .nettrace",
        ["The process id, e.g. from dotnet_processes; or else pass launch, trace or profile."] =
            "Process id; or launch, trace, profile",
        ["Instead of processId: a .dll to start, or a project to build and start, whose runtime waits at startup until the trace begins, so it covers the program from its first instruction, startup included, until it exits or seconds pass, when it is ended. For a test project, its tests run (testFilter picks them) and the trace is of the process that runs them."] =
            "Alt to processId: .dll, or project to build; starts it paused till trace begins -> covers startup to exit or seconds. Test project: runs tests (testFilter), traces test process",
        ["With launch: the program's command-line arguments, or more arguments for the test application."] =
            "W/ launch: program args, or extra test app args",
        ["With launch of a test project: the tests to run, as run_tests' filter takes them."] =
            "W/ launch test project: tests, run_tests filter syntax",
        ["Instead of processId or launch: a .nettrace file recorded earlier, such as by dotnet-trace collect on CI or another machine, to read instead of tracing; seconds does not apply. It has only the events its providers recorded: dotnet-trace's default profile has the CPU samples, exceptions, contention and collections, its gc-verbose profile the allocations, and logs and requests need the Microsoft-Extensions-Logging and System.Diagnostics.Metrics providers."] =
            "Alt to processId/launch: read earlier .nettrace; seconds ignored. Has only recorded events: dotnet-trace default = CPU samples, exceptions, contention, collections; gc-verbose = allocations; logs/requests need Microsoft-Extensions-Logging/System.Diagnostics.Metrics providers",
        ["How long to sample; the process should be busy with the work to look at meanwhile. At most this long: it ends when the process exits."] =
            "Sample secs, max; process should do target work. Ends at exit",
        ["The Profile of an earlier result, to analyze its samples again instead of sampling."] =
            "Earlier Profile to reanalyze, no sampling",
        ["The Profile of an earlier result to compare with: methods come in order of how much their share changed."] =
            "Earlier Profile to compare: methods by share change",
        ["Optional regex: counts only the threads that ran a method whose name matches, e.g. 'WorldLoop\\.OnDay'."] =
            "Opt regex: only threads running matching method, e.g. 'WorldLoop\\.OnDay'",
        ["Optional regex: leaves out the samples with a method that matches, such as waiting for a lock or for a GC, e.g. 'Monitor\\.Wait|WaitOneNoCheck|PollGC'."] =
            "Opt regex: drop samples w/ matching method (waits), e.g. 'Monitor\\.Wait|PollGC'",
        ["Optional regex: also returns the call trees under the methods that match, e.g. 'Reactive\\.Propagate'."] =
            "Opt regex: + call trees under matching methods, e.g. 'Reactive\\.Propagate'",
        ["Optional regex: also returns the call paths into the methods that match, out to their callers, e.g. 'Lock\\.TryEnterSlow'."] =
            "Opt regex: + call paths into matching methods, e.g. 'Lock\\.TryEnterSlow'",
        ["How many methods to return, those that run themselves the most first."] =
            "Methods to return, most self time first",

        // process_allocations
        ["Profiles what a running .NET process allocates, as dotnet-trace's gc-verbose profile does: for a while, samples allocations with the runtime's allocation ticks, one about every 100 KB, and estimates the bytes by thread, by type and by the stack that allocated them; and lists the types allocated that have a finalizer, which cost more than their bytes. For GC pressure: process_counters gives the allocation rate and GC counts, process_heap what stays alive. The process keeps running. With launch, it starts a program or a test project instead and profiles it from its start; finalizers then go unnoticed. With trace, it reads a .nettrace recorded earlier instead, as dotnet-trace collect --profile gc-verbose writes."] =
            "Profile allocations of running .NET process (dotnet-trace gc-verbose): ticks ~1/100 KB -> est. bytes by thread, type, stack; + finalizable types. GC pressure; rate -> process_counters, live -> process_heap. Process keeps running. launch: program|test project from start, finalizers unseen. trace: gc-verbose .nettrace",
        ["The process id, e.g. from dotnet_processes; or else pass launch."] =
            "Process id; or launch",
        ["Optional regex: counts only the threads that allocated from a method whose name matches, e.g. 'WorldLoop\\.OnDay'."] =
            "Opt regex: only threads allocating from matching method, e.g. 'WorldLoop\\.OnDay'",
        ["How many types and stacks to return, those that allocate the most first."] =
            "Types+stacks to return, most allocating first",
        ["How many frames to return per stack, from the innermost."] =
            "Frames/stack, innermost first",

        // process_calls
        ["Runs a built program for a while and counts how often methods of the libraries it uses are called, per thread: in all, per second, and per call of a marker method, such as one that runs a frame or a game day. Where process_cpu estimates time, this counts calls exactly; each counted call costs a few nanoseconds more. It runs copies of the libraries' build output with a counter added to each method, so build first; the build stays as it is, but for a deps.json beside the program while it runs. Methods of the program's own assembly cannot be counted."] =
            "Run built program, count exact calls to its libraries' methods per thread: total, /sec, per marker call. (process_cpu = time.) +few ns/call. Runs instrumented copies -> build first; build untouched, temp deps.json beside program. Own assembly not countable",
        ["The program's .dll in its build output, or its .exe beside it, e.g. 'src/Game/bin/Release/net10.0/Game.dll'."] =
            "Program .dll in build output or .exe beside, e.g. 'src/Game/bin/Release/net10.0/Game.dll'",
        ["The methods to count, by symbol path: a property counts its accessors, a type all its methods. Symbol path: a dotted name, optionally partly qualified, e.g. 'OrderService', 'OrderService.Submit(Order)' for one overload, 'OrderService.Submit.order' for a parameter or local, 'OrderService..ctor', 'Type.this[int]'. A result's Container + '.' + Name is a path."] =
            "Methods to count, symbol paths: property = accessors, type = all methods. E.g. 'OrderService.Submit(Order)', 'OrderService..ctor'",
        ["Optional: a method to count the others per call of, by symbol path, such as the one that runs a frame."] =
            "Opt marker method (symbol path): counts per its call, e.g. frame loop",
        ["The program's command-line arguments."] =
            "Program args",
        ["The directory to run the program in; by default the program's own."] =
            "Working dir; def program's",
        ["How long to run the program for; it is stopped then, unless it exited before."] =
            "Run secs; then stopped",

        // process_counters
        ["Reads the runtime counters of a running .NET process, as dotnet-counters does: CPU usage, working set, GC heap size and collection counts, allocation rate, thread pool threads and queue length, lock contention, exceptions per second and more."] =
            "Runtime counters of running .NET process, like dotnet-counters: CPU, working set, GC heap, collections, allocation rate, thread pool, lock contention, exceptions/sec",
        ["The process id, e.g. from dotnet_processes."] =
            "Process id",
        ["How long to listen; the counters report once a second."] =
            "Listen secs; 1 report/sec",

        // debug_start
        ["Starts debugging a program with netcoredbg: builds a project (or takes a .dll), sets breakpoints, runs it, and waits until it stops at a breakpoint, an unhandled exception or its entry, or exits. Returns its state: why it stopped, the call stack, the locals of the top frame, and its output so far. Ends an earlier session. Runs the program for real."] =
            "Debug w/ netcoredbg: build project|.dll, set breakpoints, run, wait for stop (breakpoint|unhandled exception|entry) or exit -> reason, stack, top locals, output. Ends prior session. Runs code",
        ["The project (.csproj) to build and run, or a .dll to run."] =
            "Project (.csproj) or .dll",
        ["Command-line arguments for the program."] =
            "Program args",
        ["Breakpoints: at the 1-based Line of the file at FilePath, or where the method Function starts, such as 'MyApp.Orders.Submit'; each with an optional Condition such as 'i == 3'."] =
            "Breakpoints: 1-based Line in FilePath, or method Function start (e.g. 'MyApp.Orders.Submit'); opt Condition",
        ["With Line: the file to break in."] =
            "W/ Line: file",
        ["The 1-based line to break at."] =
            "1-based line",
        ["Break only when this C# expression is true, such as 'i == 3'."] =
            "Break only if C# expr true, e.g. 'i == 3'",
        ["Or else: the method to break where it starts, such as 'MyApp.Orders.Submit' or 'Submit'."] =
            "Or method to break at, e.g. 'MyApp.Orders.Submit' or 'Submit'",
        ["Makes it a logpoint, which does not stop the program: each time it is hit, this message is recorded with the C# expressions in braces evaluated, such as 'i={i}, total={order.Total}', and the program goes on. The messages come with the next state, in Logged."] =
            "Logpoint, no stop: each hit logs message, C# exprs in braces evaluated, e.g. 'i={i}'. In next state's Logged",
        ["Stop at the first line of Main."] =
            "Stop at Main's first line",
        ["Exception types to stop at where they are thrown, even when caught, such as 'System.InvalidOperationException'; '*' for every exception. Unhandled exceptions always stop."] =
            "Exception types to stop at when thrown, even caught, e.g. 'System.InvalidOperationException'; '*' = all. Unhandled always stop",
        ["How long to wait for the program to stop or exit; it keeps running after that, and the state says Running."] =
            "Wait secs for stop/exit; then state Running, keeps running",

        // debug_attach
        ["Attaches the debugger to a running .NET process, such as a service that misbehaves, sets breakpoints, and waits until it stops or exits. Returns its state as debug_start does. debug_stop detaches and leaves the process running. Ends an earlier session."] =
            "Attach debugger to running .NET process, set breakpoints, wait for stop/exit. State as debug_start. debug_stop detaches, process lives. Ends prior session",

        // debug_test
        ["Debugs tests: builds and runs them with dotnet test, attaches the debugger to the test host, sets breakpoints, and waits until it stops or exits. Returns its state as debug_start does; once the test host exits, Output has the test results. Ends an earlier session; debug_stop ends the test run."] =
            "Debug tests: dotnet test, attach to test host, set breakpoints, wait for stop/exit. State as debug_start; host exit -> Output has results. Ends prior session; debug_stop ends run",
        ["The test project (.csproj)."] =
            "Test project (.csproj)",
        ["A dotnet test --filter expression for the tests to run, such as a Filter from find_tests; omit to run all of them."] =
            "dotnet test --filter expr, e.g. find_tests Filter; omit = all",
        ["How long to wait for the program to stop or exit; it keeps running after that, and the state says Running. The build comes first."] =
            "Wait secs for stop/exit, after build; then state Running, keeps running",

        // debug_set
        ["Sets a variable, field or property of the stopped program to the value of a C# expression, such as order.Total to 0, to try a fix or a case before resuming. Returns the new value."] =
            "Set variable/field/property of stopped program to C# expr (e.g. order.Total = 0) to try fix before resume. Returns new value",
        ["What to set, such as 'total' or 'order.Total'."] =
            "Target, e.g. 'order.Total'",
        ["The new value, as a C# expression such as '0', '\"text\"' or 'other.Total'."] =
            "New value, C# expr e.g. '\"text\"' | 'other.Total'",
        ["The frame, by its index in the stack: 0 is the innermost."] =
            "Frame stack index; 0=innermost",

        // debug_breakpoints
        ["Replaces the breakpoints in one file of the program being debugged, or else all the breakpoints at methods; none removes them. Returns whether the debugger could bind each one to code."] =
            "Replace breakpoints in 1 file of debugged program, or else all method breakpoints; none=remove. Returns if each bound",
        ["The file whose breakpoints to replace."] =
            "File whose breakpoints to replace",
        ["1-based lines of the file to break at."] =
            "1-based lines to break at",
        ["Or else: the methods to break at, such as 'MyApp.Orders.Submit'; replaces all the breakpoints at methods."] =
            "Or else: methods, e.g. 'MyApp.Orders.Submit'; replaces all method breakpoints",
        ["Optional conditions, one per line or method in the same order; null or empty for none."] =
            "Opt conditions, 1 per line/method, same order; null/empty=none",

        // debug_resume
        ["Resumes the program being debugged: Continue to the next breakpoint, StepOver, StepInto or StepOut; or Pause it while it runs. Waits until it stops or exits, and returns its state as debug_start does. Continue on a running program just waits again."] =
            "Resume debugged program: Continue/StepOver/StepInto/StepOut, or Pause running. Waits till stop/exit -> state as debug_start. Continue on running = wait again",
        ["Continue, StepOver, StepInto, StepOut or Pause."] =
            "Continue|StepOver|StepInto|StepOut|Pause",

        // debug_inspect
        ["Inspects the stopped program: evaluates an expression in a frame, such as 'order.Total * 2' or 'items.Count'; or lists what a variable contains, by the Reference of a variable from an earlier result; or else lists the locals of a frame."] =
            "Inspect stopped program: eval expr in frame, e.g. 'order.Total * 2'; or list var by earlier Reference; else frame locals",
        ["Optional C# expression to evaluate."] =
            "Opt C# expr to eval",
        ["Optional Reference of a variable, to list its fields, properties or items."] =
            "Opt var Reference -> list fields/props/items",

        // debug_stop
        ["Stops debugging: ends the program and the debugger."] =
            "Stop debugging: end program + debugger",

        // rename_symbol
        ["Renames a symbol declared in source, with all its references across the solution, and the file of a type that has its name. Writes the changed files to disk and returns their paths, and the lines under #if that the load leaves out and that have the old name, which it does not change."] =
            "Rename source symbol + all refs in sln + same-named type's file. Writes disk; returns paths + #if-excluded lines w/ old name (unchanged)",
        ["The new name."] =
            "New name",
        ["For a method, rename its overloads too."] =
            "Method: rename overloads too",

        // code_actions
        ["Lists the code fixes for the diagnostics at a position, line or selection and the refactorings available there, like an IDE's light bulb: add a using, implement an interface, generate a method, extract a method from the selected statements, introduce a local for the selected expression, fix a warning, and so on. Apply one with apply_code_action. Refactorings that need an IDE dialog are left out; change_signature and extract_interface run two of them. Paged."] =
            "List code fixes + refactorings at position/line/selection (IDE light bulb): add using, implement interface, generate/extract method, introduce local, fix warning... Apply via apply_code_action. Dialog ones omitted; see change_signature, extract_interface. Paged",
        ["Optional 1-based column; without it, the whole line counts."] =
            "Opt 1-based col; w/o -> whole line",
        ["Optional 1-based last line of a selection, such as the statements to extract into a method."] =
            "Opt 1-based selection last line, e.g. statements to extract",
        ["Optional 1-based column where the selection ends, exclusive; without it, the selection runs to the end of endLine."] =
            "Opt 1-based selection end col, exclusive; w/o -> end of endLine",

        // apply_code_action
        ["Applies a code action that code_actions listed, by its id; writes the changed files to disk and returns their paths. Ids hold until the code changes. An action whose title ends in '...' picks members, as an IDE would ask for in a dialog."] =
            "Apply code_actions entry by id; writes disk, returns paths. Ids valid till code changes. Title ending '...' -> picks members",
        ["The Id of an entry from code_actions."] =
            "Id from code_actions",
        ["For an action that picks members, such as 'Generate overrides...' or 'Generate constructor from members...': the members to use, by name; all it offers by default."] =
            "Member-picking action (e.g. 'Generate overrides...'): members by name; def all",

        // fix_all
        ["Fixes every diagnostic with an id at once, in one file or in the whole solution, as an IDE's 'Fix all occurrences' does; writes the changed files to disk and returns their paths."] =
            "Fix all diagnostics of 1 id in file or sln ('Fix all occurrences'); writes disk, returns paths",
        ["The diagnostic id, such as CS0168 or IDE0005."] =
            "Diagnostic id, e.g. CS0168, IDE0005",
        ["Optional C# file to fix in; without it, the whole solution."] =
            "Opt C# file; w/o -> whole sln",
        ["Which fix to apply, by its title as code_actions lists it, when there are several; the first otherwise."] =
            "Fix title per code_actions, if several; def first",

        // format
        ["Formats a C# file, or some of its lines, by the project's .editorconfig settings, and writes it to disk."] =
            "Format C# file/lines per .editorconfig; writes disk",
        ["Optional 1-based first line to format; without it, the whole file."] =
            "Opt 1-based first line; w/o -> whole file",
        ["Optional 1-based last line to format; without it, only startLine."] =
            "Opt 1-based last line; w/o -> only startLine",

        // organize_usings
        ["Removes the using directives a C# file does not need, sorts the rest as the project's .editorconfig says, and writes it to disk."] =
            "Remove unneeded usings in C# file, sort rest per .editorconfig; writes disk",
        ["Remove unneeded usings; otherwise only sort."] =
            "Remove unneeded usings; else only sort",

        // replace_symbol_body
        ["Replaces a symbol's whole declaration, such as a method with its signature, with new code; symbol_source returns the code this replaces, from the start of its first line. Writes the file to disk."] =
            "Replace symbol's whole decl (e.g. method + signature) = what symbol_source returns, from first line start. Writes disk",
        ["The new declaration, indented as it should be."] =
            "New decl, final indent",

        // insert_before_symbol
        ["Inserts code on new lines before a symbol's declaration and its documentation comment, such as a new member or a using. Writes the file to disk."] =
            "Insert code on new lines before symbol decl + doc comment (e.g. member, using). Writes disk",
        ["The code to insert, indented as it should be."] =
            "Code, final indent",

        // insert_after_symbol
        ["Inserts code on new lines after a symbol's declaration, such as a new member after an existing one. Writes the file to disk."] =
            "Insert code on new lines after symbol decl (e.g. new member). Writes disk",

        // safe_delete_symbol
        ["Deletes a symbol's declaration, but only if nothing else references it; otherwise deletes nothing and returns the references, or the code that would no longer compile without it, such as the uses of an implicit conversion, or the lines under #if that the load leaves out and that have its name. Writes the changed files to disk, and deletes a file that declared nothing but a deleted type."] =
            "Delete symbol decl if unreferenced; else nothing, returns refs, code breaking w/o it (e.g. implicit conversion uses), or #if-excluded lines w/ its name. Writes disk; deletes file left empty of types",

        // preview_edit
        ["Shows what edits would break before making them: applies text replacements in memory only, and returns the errors and warnings they would introduce and resolve, in the edited projects and in the projects that depend on them. Nothing is written."] =
            "Preview what edits break, no write: text replacements in memory -> errors+warnings added/fixed in edited + dependent projects",
        ["The replacements, applied in order: in the file at filePath, oldText becomes newText."] =
            "Replacements in order: in filePath, oldText -> newText",
        ["The C# file to edit."] =
            "C# file",
        ["Text that occurs exactly once in the file."] =
            "Text occurring exactly once in file",
        ["The text to put in its place."] =
            "Replacement text",
        ["Whether to run the project's analyzers too; slower."] =
            "Also run analyzers; slower",

        // build
        ["Builds a project or solution with dotnet build, and returns whether it succeeded, with its errors and warnings (errors first, at most 100). Output is the end of the build log when it failed without any. With timings, also where the build spent its time. With binlogPath, keeps the build's binary log for the build_* tools."] =
            "dotnet build proj/sln -> success + errors/warnings (errors first, max 100); failed w/o any -> log tail. timings -> where time went. binlogPath -> keep binlog for build_* tools",
        ["The configuration, such as Debug or Release."] =
            "Configuration, e.g. Release",
        ["Also time the build from its binary log: how long each project, target and task took, the analyzers and source generators of the compilations that ran, the evaluations and the slowest places in them, and the chain of projects that set the build's length. An up-to-date build skips most of the work, compilations included."] =
            "Also time build via binlog: per project/target/task, analyzers + source generators, evaluations + slowest spots, critical project chain. Up-to-date build skips most work",
        ["Where to write the build's binary log (.binlog), to read it with build_problems, build_target_why and the other build_* tools, or in the MSBuild Structured Log Viewer."] =
            "Binlog (.binlog) output path, for build_* tools or MSBuild Structured Log Viewer",

        // build_problems
        ["Checks a build's binary log for problems that make outputs wrong, parallel builds flaky or builds slow: files that copies from more than one source wrote (double writes: the last copy wins), projects built more than once with different global properties, and assembly version conflicts, with what depends on each version and which one ResolveAssemblyReference chose."] =
            "Check binlog for wrong outputs, flaky parallel, slow builds: double writes (copies from >1 source; last wins), projects built >1x w/ different global props, assembly version conflicts w/ dependents + ResolveAssemblyReference pick",
        ["Path to a binary log (.binlog): one that build wrote with binlogPath, or one from dotnet build -bl elsewhere, such as CI."] =
            "Binlog (.binlog) path: from build binlogPath or dotnet build -bl (e.g. CI)",

        // build_incrementality
        ["Checks that a build is incremental: builds the project twice and reports what the second build, which should have nothing to do, did anyway: the targets with Inputs and Outputs that ran, each with the output that was missing or the input that was newer, and the files it copied. Or reads the binary log of such a second build. Empty lists mean the build is incremental."] =
            "Check build incremental: build twice, report what 2nd (no-op) build did: Inputs/Outputs targets run w/ missing output or newer input, files copied. Or read such 2nd build's binlog. Empty = incremental",
        ["The project or solution to build twice; or leave this out and give binlogPath."] =
            "Proj/sln to build twice; or omit + give binlogPath",
        ["Instead of projectPath: the binary log of a build that ran right after another one."] =
            "Instead of projectPath: binlog of build run right after another",

        // build_file_origin
        ["Explains where a file that a build wrote came from: each copy to it, with its source, project, target and task; then where each source came from in turn, back to the compilation that wrote it. Copies from different sources to one file are a double write: the last one wins. Empty if no copy or compilation in the log wrote it; other tasks are not tracked."] =
            "Explain origin of build-written file: each copy to it (source, project, target, task), recursing back to compilation. Different sources = double write, last wins. Empty if no copy/compilation wrote it; other tasks untracked",
        ["The file, such as a DLL in a bin directory."] =
            "File, e.g. DLL in bin",

        // build_target_why
        ["Explains why a target ran or was skipped, in each project of a build's binary log where it appears: the chain of targets that asked for it, through DependsOnTargets, BeforeTargets or AfterTargets, up to the target the build started with; and for a target with Inputs and Outputs, the output that was missing or the input that was newer, or that its outputs were up to date. For a target that ran after another (AfterTargets), the log does not name that other target, only the target that asked for it, so the chain goes on from there. A target that is not in the log never ran: nothing asked for it, or its condition was false."] =
            "Explain why target ran/skipped, per binlog project: requester chain (DependsOnTargets/BeforeTargets/AfterTargets) to start target; Inputs/Outputs -> missing output, newer input, or up to date. AfterTargets: log names only requester. Not in log = never ran (not requested or condition false)",
        ["The target, such as CoreCompile."] =
            "Target, e.g. CoreCompile",
        ["Only projects whose file name contains this, such as App.csproj."] =
            "Only projects whose file name contains this, e.g. App.csproj",

        // build_property_trace
        ["Traces an MSBuild property or item type through a build's binary log: its value in each project after evaluation; then each change in order: reassignments during evaluation with file and line, and while targets ran, properties set in a target or output by a task, and items added, removed or output by a task, each with project, target and task. Unlike msbuild_property_trace, which reads the project, this shows what happened in that build. At most 300 changes."] =
            "Trace MSBuild property/item type in binlog: per-project value after evaluation; then changes in order: evaluation reassignments (file+line); in targets, props set/task output, items added/removed/task output (project, target, task). vs msbuild_property_trace (reads project): what that build did. Max 300 changes",
        ["The property or item type, such as DefineConstants or Compile."] =
            "Property/item type, e.g. DefineConstants | Compile",

        // build_log_search
        ["Searches a build's binary log as the MSBuild Structured Log Viewer does, and returns each match with the project, target and task it is in. Words match the text of any node: messages, properties, items, metadata, targets and tasks; \"quotes\" match a phrase. A $kind keeps one kind of node, such as '$task Csc', '$target CoreCompile', '$project App' or '$property name=LangVersion'; project(App) keeps the matches in that project, and under($project App) those in it and in the projects it built."] =
            "Search binlog like Structured Log Viewer -> matches w/ project, target, task. Words match any node text; \"quotes\"=phrase. $kind filters, e.g. '$task Csc', '$property name=LangVersion'; project(App)=in project; under($project App)=it + projects it built",
        ["The query, such as '$task Csc project(App)'."] =
            "Query, e.g. '$task Csc project(App)'",
        ["At most this many matches."] =
            "Max matches",

        // build_diff
        ["Compares two binary logs of a build, such as a passing and a failing one, or a local and a CI one: the environment variables that differ; per project, the global properties, the properties and items after evaluation, and the compiler arguments (where both compiled) that differ; the projects in only one log; and the projects whose work took the most more or less time. Projects match by their path relative to the directory that holds a log's projects, which values show as <root>, so logs from different machines compare."] =
            "Diff 2 binlogs (pass vs fail, local vs CI): env vars; per project global props, evaluated props+items, compiler args; projects in 1 log only; biggest time deltas. Projects match by path relative to <root> -> cross-machine OK",
        ["The other binary log."] =
            "Other binlog",

        // find_tests
        ["Finds NUnit, xUnit, MSTest and TUnit test methods: all of them, or those that use a symbol (for a type, any of its members), directly or through up to 3 calls in between. Each comes with its project and a Filter for run_tests. Paged."] =
            "Find NUnit/xUnit/MSTest/TUnit tests: all, or using symbol (type -> any member), direct or via <=3 calls. Each w/ project + run_tests Filter. Paged",
        ["Optional: only tests that use this symbol. The symbol, by path; or leave this out and give filePath, line and column. Symbol path: a dotted name, optionally partly qualified, e.g. 'OrderService', 'OrderService.Submit(Order)' for one overload, 'OrderService.Submit.order' for a parameter or local, 'OrderService..ctor', 'Type.this[int]'. A result's Container + '.' + Name is a path."] =
            "Opt: only tests using this symbol, by path; or omit + filePath, line, column. Path dotted, opt partly qualified, e.g. 'OrderService.Submit(Order)' (overload), 'OrderService.Submit.order' (param/local), 'OrderService..ctor', 'Type.this[int]'. Container + '.' + Name = path",

        // affected_tests
        ["Finds the tests a change can affect: maps the lines of C# files that differ from a git commit (uncommitted changes by default, new untracked files included) to the members they are in, then finds the tests that use those members, directly or through up to 3 calls in between, as find_tests does. Returns the changed members, the tests, and one Filter for run_tests that runs them all; and the members added and removed since that commit, as for reviewing a branch or writing its description."] =
            "Find tests a change affects: C# lines differing from git commit (def uncommitted, incl untracked) -> members -> tests using them direct or via <=3 calls (as find_tests). Returns changed members, tests, 1 run_tests Filter for all; + members added/removed since commit (branch review)",
        ["The git commit, branch or tag to compare the working tree with, such as HEAD, main or HEAD~3."] =
            "Git commit/branch/tag to diff working tree vs, e.g. main, HEAD~3",

        // public_api
        ["Lists the public API of a project, as other assemblies see it: its public and protected types, with their base types and interfaces, and members, with their full signatures, one line each and sorted. To compare it with an earlier version, use api_diff. Paged."] =
            "Public API of proj as other assemblies see it: public/protected types w/ bases+interfaces, members w/ full signatures; 1 line each, sorted. Compare vers -> api_diff. Paged",
        ["For a solution, the name of the project; a .csproj path picks itself."] =
            "Sln: proj name; .csproj path = itself",

        // api_diff
        ["Compares the public API of a project's current source with an earlier build of it, such as the last released version from package_assembly or a build output: the lines of public_api that were removed or changed, which can break callers, and those that were added. A changed signature shows as one removed and one added line."] =
            "Diff public API of current source vs earlier build (e.g. release from package_assembly): public_api lines removed/changed (may break callers) + added. Changed signature = 1 removed + 1 added",
        ["The path of the earlier assembly (.dll) to compare with."] =
            "Earlier assembly (.dll) path",

        // namespace_graph
        ["Maps which namespaces of the solution's source use which others, as an architecture view: each dependency with how many types of one use types of the other, and examples; and the cycles, groups of namespaces that depend on each other, which break layering. project_graph shows the same between projects."] =
            "Namespace dependency map of sln (architecture view): per dep, # using types + examples; cycles (mutually dependent namespace groups, break layering). project_graph = same per proj",
        ["Optional number of leading name parts to group namespaces by, such as 2 to count MyApp.Orders.Domain as MyApp.Orders."] =
            "Opt. # leading name parts to group by, e.g. 2: MyApp.Orders.Domain -> MyApp.Orders",
        ["Optional namespace prefix to keep, such as 'MyApp.Orders'; dependencies on other namespaces of the solution still show."] =
            "Opt. namespace prefix to keep, e.g. 'MyApp.Orders'; deps on other sln namespaces still shown",

        // change_signature
        ["Changes the parameters of a method, constructor, indexer or delegate and updates every call site, override, implementation and documentation comment across the solution, as an IDE's Change signature does: reorders, removes and adds parameters. Writes the changed files to disk and returns their paths."] =
            "Change signature of method/ctor/indexer/delegate (IDE Change signature): reorder/remove/add params; updates all call sites, overrides, impls, doc comments in sln. Writes disk; returns paths",
        ["The new parameter list in order: existing parameters by Name alone, so leaving one out removes it; new ones with Name, Type and a Value for the call sites to pass, or a DefaultValue to make them optional."] =
            "New param list in order: existing by Name only (omitted -> removed); new w/ Name, Type + Value for call sites, or DefaultValue -> optional",
        ["An existing parameter's name, or a new one's."] =
            "Existing or new param name",
        ["For a new parameter: its type, such as 'int' or 'List<string>'."] =
            "New param type, e.g. 'List<string>'",
        ["For a new parameter: the expression call sites pass, such as '0' or 'null'."] =
            "New param: expr call sites pass, e.g. '0'",
        ["For a new parameter: makes it optional with this default; call sites then pass nothing unless Value is given."] =
            "New param: default -> optional; call sites pass nothing unless Value set",

        // extract_interface
        ["Extracts an interface from a class, struct or interface: declares it with the chosen public instance members, in a new file by default, and makes the type implement it. Writes the changed files to disk and returns their paths."] =
            "Extract interface from class/struct/interface: declares it w/ chosen public instance members (def new file); type implements it. Writes disk; returns paths",
        ["The interface's name; I plus the type's name by default."] =
            "Interface name; def I+type name",
        ["The members to include, by name; all public instance members by default. A static one becomes static abstract."] =
            "Members to include by name; def all public instance. Static -> static abstract",
        ["Declare the interface in the type's file rather than in a new one."] =
            "Declare interface in type's file, not new file",

        // move_to_namespace
        ["Moves a type, or all the types of a namespace declaration, to another namespace, as an IDE's Move to namespace does: changes the declaration and updates the using directives of the code that uses them. Writes the changed files to disk and returns their paths."] =
            "Move type, or all types of namespace decl, to other namespace (IDE Move to namespace); updates users' usings. Writes disk; returns paths",
        ["The namespace to move to, such as 'MyApp.Orders'."] =
            "Target namespace, e.g. 'MyApp.Orders'",

        // run_tests
        ["Builds and runs tests with dotnet test, and returns the counts and the failures with their messages and stack traces (at most 50). Works with VSTest and, where global.json opts into it, Microsoft.Testing.Platform. When the build fails, BuildErrors says why. Output is the end of the log when there is nothing else to go on. With coverage, also the lines of each source file that no test ran. With hangTimeout, a test that hangs ends the run with a dump and the test host's threads; with repeat, the tests run several times, to tell flaky ones; with mutate, Stryker.NET changes the code of those files and reports the changes no test noticed."] =
            "Build + run tests via dotnet test -> counts, failures w/ msgs + stack traces (max 50). VSTest, or Microsoft.Testing.Platform if global.json opts in. Build fail -> BuildErrors; else nothing -> Output = log tail. coverage -> unrun lines per file. hangTimeout -> hang ends run w/ dump + test host threads. repeat -> N runs, find flaky. mutate -> Stryker.NET mutates files, reports mutations no test caught",
        ["A dotnet test --filter expression, such as a Filter from find_tests, 'FullyQualifiedName~Namespace.Class' or 'TestCategory=Fast'. Under Microsoft.Testing.Platform, xUnit v3 and TUnit take only test names this way, as find_tests gives them. Omit to run all tests."] =
            "dotnet test --filter expr, e.g. Filter from find_tests, 'FullyQualifiedName~Ns.Class'. Microsoft.Testing.Platform xUnit v3/TUnit: test names only (as find_tests gives). Omit -> all",
        ["Also measure code coverage: per source file, how many of its lines ran and which did not. Slower. Under Microsoft.Testing.Platform it needs the Microsoft.Testing.Extensions.CodeCoverage package, which MSTest.Sdk has."] =
            "Also code coverage: per file lines run/unrun. Slower. Microsoft.Testing.Platform needs Microsoft.Testing.Extensions.CodeCoverage pkg (MSTest.Sdk has)",
        ["More arguments for dotnet test, such as --filter-trait for xUnit v3 or --treenode-filter for TUnit."] =
            "Extra dotnet test args, e.g. --filter-trait (xUnit v3), --treenode-filter (TUnit)",
        ["Seconds a test may run before the run counts as hung: the test host is then dumped and ended, and Hang gives the test that ran, the dump (for process_threads, process_async or process_heap) and the test host's threads. Under Microsoft.Testing.Platform it needs the Microsoft.Testing.Extensions.HangDump package."] =
            "Secs before run = hung -> test host dumped + ended; Hang = running test, dump (for process_threads/process_async/process_heap), host threads. Microsoft.Testing.Platform needs Microsoft.Testing.Extensions.HangDump pkg",
        ["How many times to run the tests, to tell flaky ones: FailedRuns then says in how many runs each test failed, with its distinct messages. Runs after the first do not build."] =
            "Run count, find flaky tests: FailedRuns = per test # failed runs + distinct msgs. Runs after 1st skip build",
        ["C# source files to mutation test, of one project the test project references: Stryker.NET (installed on first use) changes their code in small ways, such as > to >=, runs the tests against each change, and Mutation lists the changes that no test failed on. VSTest projects; slow, minutes per file. Filter and the other options do not apply."] =
            "C# files to mutation test, of 1 proj test proj refs: Stryker.NET (installed 1st use) mutates code (e.g. > -> >=), runs tests per mutation; Mutation = changes no test failed on. VSTest only; slow, mins/file. Filter + other opts ignored",

        // msbuild_evaluate
        ["Returns MSBuild properties and items per project, as its design-time build evaluated them, such as TargetPath, DefineConstants, Compile or PackageReference. Names may contain * wildcards."] =
            "MSBuild props/items per proj from design-time eval, e.g. TargetPath, PackageReference. * wildcards OK",
        ["Property or item names, e.g. 'TargetPath', '*Version*' or 'PackageReference'."] =
            "Prop/item names, e.g. 'TargetPath', '*Version*'",

        // project_graph
        ["Lists the loaded projects in build order, each with its target framework, output type, the projects it references and the projects that reference it."] =
            "Loaded projs in build order: TFM, output type, refs, referenced-by",

        // workspace_options
        ["Reloads a project or solution for another target framework or configuration, such as net48 or Release, so that code under #if for it shows, with its diagnostics; every later call with this path uses them. Omit both to go back to the defaults: each project's first target framework, and its own configuration. Returns each project with the framework it loaded and the ones it has."] =
            "Reload proj/sln for other target framework/configuration (e.g. net48, Release) -> code under #if shows w/ diags; later calls w/ this path keep it. Omit both -> defaults (1st TFM, own cfg). Returns per proj loaded + available TFMs",
        ["The target framework, such as net48 or net10.0, for the projects that have it; the others keep their first."] =
            "TFM, e.g. net48, for projs having it; others keep 1st",
        ["The configuration, such as Release."] =
            "Configuration, e.g. Release",

        // msbuild_property_trace
        ["Explains where an MSBuild property gets its value: every place in the project and its imports, such as Directory.Build.props and the SDK's props and targets, that sets it, in evaluation order, each with its file, condition and the text it sets; and the value it ends with. Settings inside a target apply only when that target runs. Global properties and environment variables can set it too."] =
            "Trace MSBuild property value: every setter in proj + imports (Directory.Build.props, SDK props/targets) in eval order w/ file, condition, text; + final value. Setters in target apply only if it runs. Global props/env vars may set too",
        ["Path to a .csproj file."] =
            "Path to .csproj",
        ["The property, such as LangVersion or OutputPath."] =
            "Property, e.g. LangVersion",

        // packages
        ["Lists the NuGet packages of the projects with dotnet list package: all of them, or only the outdated ones (with their latest version), the vulnerable ones (with severities and advisories) or the deprecated ones; transitive packages too, if asked. Outdated, Vulnerable and Deprecated ask the package sources. Paged."] =
            "NuGet pkgs via dotnet list package: all, or Outdated (+latest), Vulnerable (+severity, advisories), Deprecated; opt. transitive. Non-All query sources. Paged",
        ["All, Outdated, Vulnerable or Deprecated."] =
            "All|Outdated|Vulnerable|Deprecated",
        ["Include transitive packages."] =
            "Incl. transitive pkgs",

        // package_search
        ["Searches the project's NuGet package sources, as its nuget.config sets them, for packages by keywords or id. Returns each package's latest version, description, authors, total downloads and whether its id prefix is verified. Paged."] =
            "Search NuGet sources (nuget.config) by keywords/id -> latest ver, desc, authors, total downloads, id prefix verified. Paged",
        ["Search terms, e.g. 'json serializer', or a package id."] =
            "Search terms, e.g. 'json serializer', or pkg id",
        ["Include prerelease versions."] =
            "Incl. prerelease",

        // package_info
        ["Describes a NuGet package from the project's package sources without downloading it: its versions, newest first, and for one version its description, license, deprecation, known vulnerabilities, the target frameworks it has dependencies for, the nearest of those for each of the project's target frameworks, and its dependencies there."] =
            "NuGet pkg info from sources, no download: vers newest 1st; per ver: desc, license, deprecation, vulnerabilities, dep TFMs, nearest per proj TFM + deps there",
        ["Package id, e.g. 'Newtonsoft.Json'."] =
            "Pkg id, e.g. 'Newtonsoft.Json'",
        ["Package version, e.g. '13.0.3'; the latest stable one by default."] =
            "Ver, e.g. '13.0.3'; def latest stable",
        ["Include prerelease versions, also in picking the latest."] =
            "Incl. prerelease, also picking latest",
        ["Target framework to pick for, e.g. 'net8.0'; by default those of the project, or of the solution's projects."] =
            "TFM, e.g. 'net8.0'; def proj's/sln projs'",

        // package_assembly
        ["Downloads a NuGet package into the global packages folder, where restore finds it later, and returns the paths of its assemblies for each of the project's target frameworks, and of its readme. decompile, assembly_sources, assembly_il and assembly_asm take those paths, so a package's API can be read before adding it. Its documentation comments, if any, sit next to each assembly in a .xml file. Symbols tells per assembly whether it has a PDB (looking on the symbol servers too) and SourceLink, so its original source can be read."] =
            "Download NuGet pkg to global packages folder (restore reuses) -> assembly paths per proj TFM + readme; feed to decompile/assembly_sources/assembly_il/assembly_asm to read API before adding. Doc comments: .xml beside assembly if any. Symbols: per assembly PDB (symbol servers too) + SourceLink -> original source",

        // package_upgrade_check
        ["Checks what moving NuGet packages to other versions would break, without changing anything: compiles the projects that reference them against the other versions' assemblies and returns the compiler errors and warnings that would be new, such as uses of removed or obsolete APIs. Dependencies are not resolved: a version that needs a newer version of another package shows that as an error such as CS1705, so include that package too. Paged."] =
            "Upgrade check, no changes: compile referencing projs vs other pkg vers' assemblies -> new compiler errs/warns (removed/obsolete APIs). Deps not resolved: needs newer dep -> e.g. CS1705 -> include it too. Paged",
        ["Packages as 'Id@Version', or just 'Id' for the latest stable version, e.g. ['Newtonsoft.Json@13.0.3']."] =
            "'Id@Version' or 'Id' (latest stable), e.g. ['Newtonsoft.Json@13.0.3']",

        // package_why
        ["Explains why a NuGet package is in the project's dependency graph, from what restore resolved: each chain of dependencies from a direct dependency of the project down to the package, per target framework, with resolved versions; and restore's warnings and errors about it, such as downgrades (NU1605), version conflicts (NU1107) or vulnerabilities (NU1901-NU1904). Empty if nothing depends on it."] =
            "Why NuGet pkg in dep graph (from restore): chains direct dep -> pkg per TFM w/ resolved vers; + restore warns/errs e.g. downgrade NU1605, conflict NU1107, vulnerability NU1901-NU1904. Empty if nothing depends",
        ["Most dependency paths to return."] =
            "Max dep paths",

        // package_contents
        ["Downloads a NuGet package into the global packages folder and lists its files, with what reaches the build of a project that references it: the MSBuild .props and .targets files restore imports, per target framework of the project, and its analyzers and source generators. The files can be read from the package's directory."] =
            "Download NuGet pkg to global packages folder; list files + what reaches referencing build: .props/.targets restore imports per TFM, analyzers, source generators. Files readable in pkg dir",
        ["Only list files in this folder of the package, e.g. 'build' or 'lib/net8.0'; packages can hold hundreds."] =
            "Only files in this pkg folder, e.g. 'lib/net8.0'; pkgs may hold hundreds",

        // package_licenses
        ["Lists the license of every NuGet package the projects resolved, transitive ones included, from what restore wrote and the packages' own metadata in the packages folders, without network access. Paged."] =
            "License of every resolved NuGet pkg incl. transitive, from restore output + pkg metadata in packages folders; no network. Paged",

        // nuget_config
        ["Returns the NuGet configuration restore uses for the project: the nuget.config files that apply, the package sources and whether they have credentials (never the credentials), package source mapping, the global packages folder and the fallback folders. Helps with restore errors such as NU1101 (package not found) or NU1301 (source unreachable)."] =
            "NuGet cfg restore uses: applicable nuget.config files, sources + has-credentials (never credentials), source mapping, global packages folder, fallback folders. For restore errs e.g. NU1101 (not found), NU1301 (unreachable)",

        // diagnostics
        ["Returns compiler and analyzer diagnostics (errors first) for the whole project/solution or a single file, with the severities the project's .editorconfig and build settings give them; for a project that did not load, such as one whose restore failed, the MSBuild errors that say why; and warnings where the server's compiler is older than the SDK's: options it does not know, such as a newer language version, and analyzers or generators it cannot load. With analyzerPackages, also the rules of analyzer packages the project does not reference, without changing it; with allRules, every rule, also those turned off; with aot, the trim, native AOT and single-file warnings (IL2xxx, IL3xxx) that publishing with PublishTrimmed or PublishAot would give. Paged."] =
            "Compiler + analyzer diagnostics (errs 1st), proj/sln or 1 file, severities per .editorconfig + build settings. Unloaded proj (e.g. restore failed) -> MSBuild errs why. Server compiler older than SDK -> warns: unknown options (newer langversion), unloadable analyzers/generators. analyzerPackages -> rules of unreferenced analyzer pkgs, proj unchanged; allRules -> every rule incl. off; aot -> trim/native AOT/single-file warns (IL2xxx, IL3xxx) PublishTrimmed/PublishAot would give. Paged",
        ["Optional path of a C# file to limit diagnostics to."] =
            "Opt. C# file to limit to",
        ["The least severity to return: Hidden, Info, Warning or Error."] =
            "Min severity: Hidden|Info|Warning|Error",
        ["Whether to run the project's analyzers too, such as the .NET code style and quality analyzers; slower."] =
            "Also run proj analyzers (e.g. .NET style/quality); slower",
        ["NuGet packages of analyzers to run as well, such as 'Roslynator.Analyzers', 'Meziantou.Analyzer' or 'Microsoft.CodeAnalysis.NetAnalyzers@10.0.100' (id, optionally @version, else the latest); downloaded into the global packages folder from the project's package sources. code_actions knows only the project's own analyzers."] =
            "Extra analyzer NuGet pkgs, e.g. 'Roslynator.Analyzers', 'Microsoft.CodeAnalysis.NetAnalyzers@10.0.100' (id, opt. @version, else latest); downloaded to global packages folder from proj sources. code_actions knows proj's own analyzers only",
        ["Turns on every rule of the analyzers, also those off by default or turned off by .editorconfig, a global config or the SDK's analysis level, as warnings (or errors, if that is their default); combine with ids to keep the result short."] =
            "Enable every analyzer rule, incl. default-off or off via .editorconfig/global config/SDK analysis level, as warns (errs if default); combine w/ ids to keep short",
        ["Also runs the trim, native AOT and single-file analyzers, as IsAotCompatible turns them on, without changing the project: the code a trimmed or native AOT publish would break, such as reflection, Type.GetType by name and dynamic code. Downloads Microsoft.NET.ILLink.Tasks into the global packages folder."] =
            "Also run trim/native AOT/single-file analyzers (as IsAotCompatible), proj unchanged: code trimmed/AOT publish breaks, e.g. reflection, Type.GetType by name, dynamic code. Downloads Microsoft.NET.ILLink.Tasks to global packages folder",
        ["Optional regex for the diagnostic ids to return, e.g. '^CA18' or '^(RCS|MA)'."] =
            "Opt. regex for diag ids, e.g. '^CA18'",

        // assembly_il
        ["Disassembles types or methods of any .NET assembly, such as a build output or a dependency, to IL. A type stands for all its methods. Every method comes with its compiler-generated helpers (async and iterator state machines, lambdas, local functions). Paged per method."] =
            "Disassemble types/methods of any .NET assembly (build output, dep) to IL. Type = all methods. Incl. compiler-generated helpers (async/iterator state machines, lambdas, local fns). Paged per method",
        ["Path to the .dll or .exe."] =
            "Path to .dll/.exe",
        ["Type or method name, optionally partially qualified, e.g. 'System.String.Concat', 'Dictionary.TryGetValue' or 'Outer.Nested'; or a documentation ID such as 'M:Ns.Type.Method(System.Int32)'. A method name returns all its overloads. A generic arity, as in 'List`1' or 'List<T>', must match; without one, any arity does."] =
            "Type/method name, opt. partially qualified, e.g. 'Dictionary.TryGetValue'; or doc ID 'M:Ns.Type.Method(System.Int32)'. Method -> all overloads. Generic arity ('List`1', 'List<T>') must match; none -> any",

        // symbol_il
        ["Compiles the declaring project from its current source and disassembles a type, method, property or event declared in it to IL. A type stands for all its methods. Every method comes with its compiler-generated helpers (async and iterator state machines, lambdas, local functions). Paged per method."] =
            "Compile declaring proj from current source, disassemble declared type/method/property/event to IL. Type = all methods. Incl. compiler-generated helpers (async/iterator state machines, lambdas, local fns). Paged per method",

        // assembly_asm
        ["Disassembles the native code the JIT generates for types or methods of any .NET assembly, such as a build output or a dependency. The code comes from the JIT of the .NET the assembly is built for (its newest installed patch; for a framework assembly, its own framework), or else of the nearest installed .NET, fully optimized, for this machine's CPU; the listing names it. Assemblies built for debugging compile unoptimized (MinOpts). Generic code is shown for reference-type arguments, as the code they all share (System.__Canon). A type stands for all its methods. Every method comes with its compiler-generated helpers (async and iterator state machines, lambdas, local functions). Paged per method."] =
            "Disassemble JIT native code of types/methods in any .NET assembly (build output/dependency). JIT: target .NET (newest patch; fw assembly -> own fw) else nearest installed; full opt, this CPU; listing names it. Debug build -> MinOpts. Generics: ref-type args -> shared System.__Canon. Type = all methods. +generated helpers (async/iterator state machines, lambdas, local fns). Paged/method",

        // symbol_asm
        ["Compiles the declaring project from its current source and disassembles the native code the JIT generates for a type, method, property or event declared in it. The code comes from the JIT of the .NET the assembly is built for (its newest installed patch; for a framework assembly, its own framework), or else of the nearest installed .NET, fully optimized, for this machine's CPU; the listing names it. Assemblies built for debugging compile unoptimized (MinOpts). Generic code is shown for reference-type arguments, as the code they all share (System.__Canon). A type stands for all its methods. Every method comes with its compiler-generated helpers (async and iterator state machines, lambdas, local functions). Paged per method. With typeArguments, generic code compiles for those instead, as each value type gets code of its own. With pgoWorkload, the code is what long-running code gets: Tier1, with Dynamic PGO's guarded devirtualization and block layout, and static readonly fields as constants."] =
            "Compile declaring project from current source; disassemble JIT native code of its type/method/property/event. JIT: target .NET (newest patch) else nearest installed; full opt, this CPU; listing names it. Debug build -> MinOpts. Generics: ref-type args -> shared System.__Canon. Type = all methods. +generated helpers (async/iterator state machines, lambdas, local fns). Paged/method. typeArguments -> generics for those (value types own code). pgoWorkload -> Tier1 code w/ Dynamic PGO guarded devirtualization, block layout, static readonly as constants",
        ["Type arguments for generic code, as code where the symbol is declared would write them, such as ['float'] or ['int', 'Vec2?']: the outermost type's parameters first, then a generic method's own. Those left out are reference types, which share one code."] =
            "Generic type args as written at decl, e.g. ['int', 'Vec2?']: outermost type's first, then method's own. Omitted -> ref types (shared code)",
        ["A parameterless method of the solution, by symbol path, such as a test or a benchmark, to run first: again and again, until the JIT has compiled what it calls often at Tier1 with what Dynamic PGO saw, for up to 20 seconds. Each listing is then of the last tier its method reached. A method that is not static runs on an instance from its type's parameterless constructor."] =
            "Parameterless solution method (symbol path; test/benchmark) run repeatedly first until JIT reaches Tier1 w/ Dynamic PGO, max 20 seconds. Listing = last tier. Non-static -> instance via parameterless ctor",

        // type_layout
        ["Shows where the runtime puts the fields of a class or struct declared in the solution, compiled from current source: each field's offset, as the JIT's code addresses it (symbol_asm), and size, base types' fields included; the padding between them; the size of an object or struct; and the 64-byte cache line each field starts in. Laid out by the .NET the project is built for, as symbol_asm compiles with, without running any of its code."] =
            "Field layout of solution class/struct, current source: offset (as symbol_asm code addresses) + size per field, base fields incl; padding; object/struct size; 64-byte cache line per field. Target .NET layout; no code run",
        ["The class or struct, by symbol path. Symbol path: a dotted name, optionally partly qualified, e.g. 'OrderService', 'OrderService.Submit(Order)' for one overload, 'OrderService.Submit.order' for a parameter or local, 'OrderService..ctor', 'Type.this[int]'. A result's Container + '.' + Name is a path."] =
            "Class/struct symbol path: dotted, opt partly qualified: 'OrderService.Submit(Order)' overload, 'OrderService.Submit.order' param/local, 'OrderService..ctor', 'Type.this[int]'. Container+'.'+Name = path",
        ["Type arguments for generic code, as code where the symbol is declared would write them, such as ['float'] or ['int', 'Vec2?']: the outermost type's parameters first, then a generic method's own. Those left out are System.Object, as all reference types lay out alike."] =
            "Generic type args as written at decl, e.g. ['int', 'Vec2?']: outermost type's first, then method's own. Omitted -> System.Object (ref types lay out alike)",

        // decompile
        ["Decompiles types or members of any .NET assembly to C# with ILSpy's decompiler, such as a dependency without source. definition gives the AssemblyPath of a symbol from a referenced assembly. A type comes out whole, with its nested types. This shows what the assembly really contains: code from source generators, members the compiler synthesizes and code IL weavers add. Local variables keep their names if a PDB is at hand. For the original source with comments, use assembly_sources or symbol_source. With lowered, it shows the C# the compiler lowered the code to instead: lambdas as closure classes, async and iterator methods as state machines (with their generated types), foreach, using, lock and string interpolation spelled out: where hidden allocations, boxing and captures come from. Paged."] =
            "Decompile .NET assembly types/members to C# (ILSpy), e.g. dependency w/o source; AssemblyPath via definition. Type whole + nested. Real contents: source generator output, synthesized members, IL weaving. Locals named if PDB. Original source/comments -> assembly_sources/symbol_source. lowered -> lowered C#: closures, async/iterator state machines, foreach/using/lock/interpolation expanded -> hidden allocations, boxing, captures. Paged",
        ["Type or member name, optionally partially qualified, e.g. 'System.Linq.Enumerable', 'Dictionary.TryGetValue' or 'Outer.Nested'; or a documentation ID such as 'M:Ns.Type.Method(System.Int32)'. A method name returns all its overloads. A generic arity, as in 'List`1' or 'List<T>', must match; without one, any arity does."] =
            "Type/member name, opt partly qualified ('Dictionary.TryGetValue'), or doc ID ('M:Ns.Type.Method(System.Int32)'). Method -> all overloads. Generic arity ('List`1'/'List<T>') must match; none -> any",
        ["Show the lowered code, as the compiler emits it, with the compiler-generated types of a method's lambdas and state machines; build the project first to see its current source this way."] =
            "Lowered code as compiler emits, w/ generated lambda/state machine types; build first for current source",

        // assembly_sources
        ["Lists the source files of any .NET assembly from its PDB, which comes from next to the assembly, from inside it, or from the Microsoft or NuGet symbol server. With a name or member, also returns their original source: embedded in the PDB, from disk, or through SourceLink from the repository at the commit it was built from; Verified tells whether a file has the checksum the PDB records. Unlike decompile, this keeps comments and original names, but lacks what the compiler synthesizes (records, async, lambdas) or IL weavers add; PDBs usually embed the code of source generators. Paged."] =
            "List .NET assembly source files from PDB (beside, embedded, Microsoft/NuGet symbol server). name/member -> also original source: PDB-embedded, disk, or SourceLink at build commit; Verified = PDB checksum match. vs decompile: keeps comments/names; lacks synthesized code (records, async, lambdas), IL weaving; generator code usually embedded. Paged",
        ["Optional path, or part of it, of the files to return the source of, e.g. 'Parser.cs' or 'src/Json/'."] =
            "Opt file path/part, e.g. 'Parser.cs', 'src/Json/'",
        ["Optional type or member whose files to return the source of, named as for decompile, e.g. 'JsonConvert' or 'JsonConvert.SerializeObject'."] =
            "Opt type/member whose files to return (decompile naming), e.g. 'JsonConvert.SerializeObject'",

        // resolve_stack_trace
        ["Maps a .NET stack trace, such as from an exception's ToString(), a log, a test failure or CI output, to the solution's current source: per frame, the symbol path of the member it ran in, where compiler-generated frames name their method (an async or iterator MoveNext, a lambda's <>c__DisplayClass, a local function's <Method>g__Local), and its file and line with the text of that line. A frame without a line, as from a Release build without PDBs, points at the method's declaration; a line that is no longer in that member is flagged, as when the file changed since the build or the trace comes from a build machine. Frames of code outside the solution have no symbol."] =
            "Map .NET stack trace (exception, log, test failure, CI) to current source: per frame member symbol path (generated frames -> owning method: MoveNext, <>c__DisplayClass, <Method>g__Local), file, line + text. No line (Release w/o PDB) -> method decl; line outside member flagged (stale). External frames: no symbol",
        ["The stack trace text: lines such as '   at MyApp.Orders.<SubmitAsync>d__4.MoveNext() in C:\\src\\Orders.cs:line 42'; other lines are skipped."] =
            "Trace text, lines like '   at MyApp.Orders.<SubmitAsync>d__4.MoveNext() in C:\\src\\Orders.cs:line 42'; others skipped",

        // process_async
        ["Shows the async methods of a running .NET process or a dump that wait, as SOS's dumpasync does: chains of awaiting methods from the innermost out to the one that started them, each with the await it waits at (a line, where the PDB is at hand), and what the innermost waits for, such as a SemaphoreSlim, a TaskCompletionSource or a Task.Delay; chains of the same shape once, with their count. For hangs in async code, where process_threads shows idle threads, since an awaiting method has no thread. Walks the heap, so it takes a while on a big one."] =
            "Waiting async methods in running .NET process/dump (like SOS dumpasync): await chains innermost -> starter, await line each (if PDB), what innermost awaits (SemaphoreSlim, TaskCompletionSource, Task.Delay); same shape once + count. Async hangs where process_threads shows idle threads. Walks heap: slow if big",
        ["Optional regex: only the chains with a method whose path matches, e.g. 'Orders\\.Submit'."] =
            "Opt regex on chain method paths, e.g. 'Orders\\.Submit'",
        ["How many chains to return, those that wait most often first."] =
            "Max chains, most frequent first",

        // process_dump
        ["Writes a dump of a running .NET process to a file, as dotnet-dump collect does, to read with process_threads, process_async, process_heap, process_objects, process_roots and dotnet_processes after the process has moved on, exited or restarted, or to compare two moments. The process pauses while it is written. Returns the file's path and size."] =
            "Dump running .NET process to file (like dotnet-dump collect) for process_threads, process_async, process_heap, process_objects, process_roots, dotnet_processes later / compare moments. Process pauses. Returns path + size",
        ["Mini (threads only), Heap (what the process tools read), Triage or Full (the whole memory, larger)."] =
            "Mini (threads), Heap (process tools), Triage, Full (all memory)",
        ["Where to write it; by default a new file under the temporary directory's Roslynaut/Dumps."] =
            "Output path; def new file in temp Roslynaut/Dumps",

        // process_events
        ["Listens to a running .NET process, or a program or test project it starts with launch, for a while, and summarizes one kind of runtime event. Exceptions: those thrown, by type and the method that threw them, with a message, the stack and where they were caught, also those caught and logged nowhere. Logs: the Microsoft.Extensions.Logging messages from Debug up, without a restart (needs the EventSource logger, which the default hosts add). Contention: the time threads waited for locks, by the stack that waited. Collections: the garbage collections with their pauses and reasons, and who called GC.Collect. Requests: the HTTP requests served (by route) and sent (by host) with their p50, p95 and p99 durations, from .NET 8. The process keeps running. With trace, it reads a .nettrace recorded earlier instead, such as on CI."] =
            "Listen to running .NET process (or launch program/test project), summarize 1 runtime event kind. Exceptions: type + thrower, message, stack, catch site, incl swallowed. Logs: Microsoft.Extensions.Logging Debug+, no restart (needs EventSource logger, default hosts add). Contention: lock wait by stack. Collections: garbage collections, pauses, reasons, GC.Collect callers. Requests: HTTP served (route)/sent (host) p50/p95/p99, .NET 8+. Process keeps running. trace -> read earlier .nettrace (CI)",
        ["Exceptions, Logs, Contention, Collections or Requests."] =
            "Exceptions, Logs, Contention, Collections or Requests",
        ["How long to listen; the process should do the work to look at meanwhile. At most this long: it ends when the process exits."] =
            "Listen duration (max; ends on process exit). Process should do work meanwhile",
        ["Optional regex: the exception types or stacks, the log categories, the waiting stacks or the routes and hosts to keep, e.g. 'MyApp\\.'."] =
            "Opt regex: exception types/stacks, log categories, wait stacks, routes/hosts, e.g. 'MyApp\\.'",
        ["For Logs: the least level, Debug, Information, Warning, Error or Critical."] =
            "Logs min level: Debug, Information, Warning, Error, Critical",
        ["How many groups, messages, stacks or collections to return."] =
            "Max groups/messages/stacks/collections",

        // call_paths
        ["Finds how one member reaches another through calls: the shortest call chains from 'from' to 'to', each hop with the line of its call, and the member it calls through where that is an interface or base member, as for 'how does this request reach the database' or 'can this code call that method'. Follows callers back from 'to', so calls through interfaces and overrides count; a lambda's calls count as its member's."] =
            "Shortest call chains 'from' -> 'to', each hop: call line + interface/base member called via ('how request reaches database'). Walks callers back from 'to': interface/override calls count; lambda calls = its member's",
        ["The member the chains start at, by symbol path. Symbol path: a dotted name, optionally partly qualified, e.g. 'OrderService', 'OrderService.Submit(Order)' for one overload, 'OrderService.Submit.order' for a parameter or local, 'OrderService..ctor', 'Type.this[int]'. A result's Container + '.' + Name is a path."] =
            "Chain start member, symbol path: dotted, opt partly qualified: 'OrderService.Submit(Order)' overload, 'OrderService.Submit.order' param/local, 'OrderService..ctor', 'Type.this[int]'. Container+'.'+Name = path",
        ["The member the chains end at, by symbol path."] =
            "Chain end member, symbol path",
        ["The most calls a chain may have."] =
            "Max calls per chain",
        ["How many chains to return, shortest first."] =
            "Max chains, shortest first",

        // control_flow
        ["The control flow graph of a method, constructor or accessor, as the compiler builds it: its basic blocks with their lines, the condition each one branches on and where each outcome goes, where it returns or throws, which blocks cannot be reached, and the try, catch and finally each is in. With toLine, the conditions that hold on each path from the start to that line, as for what input reaches a throw, or which branch a test has to take to run a line coverage reports as not run. Lambdas and local functions inside are not followed."] =
            "Control flow graph of method/ctor/accessor: basic blocks + lines, branch conditions + targets, returns/throws, unreachable blocks, try/catch/finally. toLine -> conditions per path to line (input reaching throw; branch to cover line coverage missed). Nested lambdas/local fns not followed",
        ["A 1-based line in the member: also the paths to it from the start, as the conditions along each."] =
            "1-based line in member: also start->line paths as conditions",

        // throws
        ["Finds the exceptions that can leave a member: those it throws itself, those that members it calls throw (followed into source up to depth calls deep), and those that methods of referenced assemblies document with <exception>; each with where in the member it comes from and the chain of calls to the throw. Exceptions a catch on the way handles are left out. For error handling and tests, and what an API throws. It does not see exceptions the runtime raises, such as NullReferenceException, nor follow calls through interfaces or virtual members to their implementations."] =
            "Exceptions leaving member: own throws, callees' (source, to depth), referenced <exception> docs; each w/ origin + call chain. Caught omitted. For error handling, tests. Misses runtime exceptions (NullReferenceException), interface/virtual dispatch",
        ["How many calls deep to follow members declared in source."] =
            "Call depth into source members",

        // benchmark
        ["Measures how fast pieces of C# are with BenchmarkDotNet, in Release, against the project's current code: per case, the mean time with its error and standard deviation, the ratio to the first case, and the bytes allocated per operation with the Gen0 collections. Pass cases to compare implementations, as before keeping an optimization; or leave them out to run the project's own [Benchmark] classes, if it is a BenchmarkDotNet project. A short job takes about a minute per case; differences within the error are noise."] =
            "Benchmark C# w/ BenchmarkDotNet, Release, current code: per case mean, error, std dev, ratio to first, bytes allocated/op, Gen0. cases -> compare impls; omit -> project's [Benchmark] classes. Short ~1 min/case; diff within error = noise",
        ["Path to a .csproj: the project whose code the cases use, or a BenchmarkDotNet project to run."] =
            ".csproj: project cases use, or BenchmarkDotNet project to run",
        ["The cases by name, each C# code to measure: an expression whose value is kept, such as 'Parser.Parse(text)', or statements ending in ';'. The first is the baseline. They can use the project's code and what setup declares."] =
            "Named cases, C# to measure: expression (value kept), e.g. 'Parser.Parse(text)', or statements ending ';'. First = baseline. May use project code + setup",
        ["C# members of the benchmark class that the cases use, set up once, such as 'string text = File.ReadAllText(\"in.txt\");' or a [GlobalSetup] method."] =
            "Benchmark class members for cases, set up once, e.g. 'string text = File.ReadAllText(\"in.txt\");' or [GlobalSetup] method",
        ["Using directives' namespaces for the cases, such as 'MyApp.Parsing'."] =
            "Using namespaces for cases, e.g. 'MyApp.Parsing'",
        ["Without cases: which of the project's benchmarks to run, as BenchmarkDotNet's --filter takes it, such as '*Parser*'."] =
            "No cases: benchmarks to run, BenchmarkDotNet --filter syntax, e.g. '*Parser*'",
        ["Short (3 iterations, quick and rough) or Default (until the results are stable, minutes per case)."] =
            "Short (3 iterations, rough) or Default (until stable, minutes/case)",

        // aspnet_endpoints
        ["Lists the HTTP endpoints of ASP.NET Core projects from their source: controller actions, with their [Route] and [Http*] templates combined with the controller's, and minimal API Map* calls, with the prefixes of the MapGroup calls they hang off; each with its HTTP method, route, handler and the attributes or calls that add to it, such as [Authorize] or RequireAuthorization. Maps a URL from a log or a bug report to the code that handles it. Routes built at run time, not from constants, show as '?'."] =
            "ASP.NET Core HTTP endpoints from source: controller actions ([Route]+[Http*] w/ controller's), minimal API Map* (+MapGroup prefixes); per endpoint HTTP method, route, handler, modifiers ([Authorize], RequireAuthorization). URL from log/bug -> handler. Runtime-built routes: '?'",
        ["Optional regex for the routes to list, e.g. '^/api/orders'."] =
            "Opt route regex, e.g. '^/api/orders'",

        // di_registrations
        ["Lists the Microsoft.Extensions.DependencyInjection registrations in source, per service type: each AddSingleton, AddScoped, AddTransient, TryAdd*, AddKeyed* and AddHostedService call with its implementation (a type, or the factory or instance it gives), lifetime and location; which registration wins, the last in source order, as a resolve takes the last; the constructors the service is injected into; and captive dependencies, a scoped or transient service injected into a singleton. Answers which class runs when an interface is injected. Registrations made by scanning assemblies, or through helpers that take the types as parameters, are not seen."] =
            "Microsoft.Extensions.DependencyInjection registrations in source by service type: AddSingleton/AddScoped/AddTransient/TryAdd*/AddKeyed*/AddHostedService w/ impl (type/factory/instance), lifetime, location; winner = last; injecting ctors; captive dependencies (scoped/transient in singleton). Which class injected interface gets. Misses assembly scanning, type-param helpers",
        ["Optional regex for the service types to list, e.g. 'IOrderStore' or '^MyApp\\.'."] =
            "Opt service type regex, e.g. '^MyApp\\.'",

        // find_duplicates
        ["Finds copied code in the solution's C# files: runs of at least minStatements statements in a row that occur more than once, word for word or with other names and literals (Renamed), each run as long as the copies agree, with the files, lines and members of every copy, the most code first. Before a refactoring, or to find every copy of code a bug was just fixed in. Generated files are left out."] =
            "Find duplicate/copied C# code: >= minStatements consecutive statements repeated verbatim or w/ other names/literals (Renamed), maximal; files, lines, members per copy, biggest first. Pre-refactoring / copies of fixed bug. Generated files excluded",
        ["The fewest statements in a row that count as a copy."] =
            "Min consecutive statements per copy",
        ["How many groups of copies to return."] =
            "Max copy groups",

        // verify_pending
        ["Lists the snapshots that Verify tests received and that do not match the verified ones: each *.received.* file beside its *.verified.* file, with the diff from the verified file to the received one, or the text of a new snapshot. accept makes received files the verified ones, reject deletes them. Run after run_tests, and accept only a snapshot whose diff is the intended change."] =
            "Verify snapshots received != verified: each *.received.* beside *.verified.* + diff, or new snapshot text. accept -> verified; reject -> delete. After run_tests; accept only intended diffs",
        ["A .csproj, .sln or .slnx file, or a directory: the received files are looked for below its directory, outside bin and obj."] =
            ".csproj, .sln, .slnx or dir: searched below, excl bin/obj",
        ["The received files to accept, as Received names them, or '*' for all."] =
            "Received files to accept (Received names) or '*'",
        ["The received files to reject, which deletes them, as Received names them, or '*' for all."] =
            "Received files to reject (deletes; Received names) or '*'",

        // regex_test
        ["Runs a .NET regular expression against sample inputs, as System.Text.RegularExpressions.Regex does, not another engine's dialect: whether each input matches, each match with its position and its groups by number and name, and with replacement what Regex.Replace returns. For writing or checking a pattern before it goes into code, such as into [GeneratedRegex]; no evaluate needed."] =
            "Test .NET regex (System.Text.RegularExpressions.Regex dialect) on sample inputs: match?, positions, groups by number/name; replacement -> Regex.Replace output. Check pattern before code/[GeneratedRegex]; no evaluate needed",
        ["The pattern as the string the code passes, after C# unescapes it: \\d+ for @\"\\d+\" or \"\\\\d+\"."] =
            "Pattern after C# unescape: \\d+ for @\"\\d+\" or \"\\\\d+\"",
        ["The inputs to match against."] =
            "Inputs to match",
        ["RegexOptions, comma-separated, e.g. 'IgnoreCase, Multiline'; also Singleline, ExplicitCapture, IgnorePatternWhitespace, RightToLeft, ECMAScript, CultureInvariant and NonBacktracking."] =
            "RegexOptions comma-separated: IgnoreCase, Multiline, Singleline, ExplicitCapture, IgnorePatternWhitespace, RightToLeft, ECMAScript, CultureInvariant, NonBacktracking",
        ["Optional replacement, as Regex.Replace takes it: $1, ${name}, $0 or $& for the whole match, $$ for a dollar sign."] =
            "Opt Regex.Replace replacement: $1, ${name}, $0/$& whole match, $$ dollar",
        ["How many matches to return per input."] =
            "Max matches per input",

        // resources
        ["Checks the projects' .resx resource files against their translations, Strings.resx against Strings.de.resx and the like: per culture the string resources it lacks, which fall back to the neutral text at run time, and those only it has, which nothing reads. With unused, also the resources no C# code uses, through the generated class's property (Strings.Key) or by name in a string literal, as localizer[\"Key\"] and GetString(\"Key\") do; Razor code counts through the Razor generator's output. Resources used only from XAML or by names built at run time count as unused."] =
            "Check .resx vs translations (Strings.resx vs Strings.de.resx): per culture missing strings (neutral fallback) + extras (unread). unused -> also resources no C# uses via generated property (Strings.Key) or literal (localizer[\"Key\"], GetString(\"Key\")); Razor via generator output. XAML-only/runtime names = unused",
        ["Optional .resx file, neutral or of a culture: checks only its set."] =
            "Opt .resx (neutral/culture): check only its set",
        ["Whether to look for the resources no C# code uses; reads all the code."] =
            "Find resources unused by C#; reads all code",
    }.ToFrozenDictionary();
}
