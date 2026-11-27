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
            "MSBuild info per project: target framework, key properties, project + package refs; for project that failed to load, MSBuild errors explaining why (e.g. restore not run).",
        ["Path to a .csproj, .sln or .slnx file."] =
            "Path to .csproj, .sln or .slnx.",

        // workspace_symbols
        ["Searches type and member declarations by name, matching substrings and camel-case humps like an IDE's 'Go to symbol'. Paged."] =
            "Search type/member declarations by name; substring + camel-case hump match, like IDE 'Go to symbol'. Paged.",
        ["Name or pattern to search for, e.g. 'Workspace' or 'GDA' for 'GetDocumentAsync'. May be qualified with namespaces and containing types, e.g. 'Tools.WorkspaceService.GetDocumentAsync'."] =
            "Name or pattern, e.g. 'Workspace' or 'GDA' for 'GetDocumentAsync'. May qualify with namespaces/containing types, e.g. 'Tools.WorkspaceService.GetDocumentAsync'.",
        ["0-based page number; hasMore in the result tells whether a next page exists."] =
            "0-based page number; result hasMore = next page exists.",
        ["Number of results per page."] =
            "Results per page.",

        // document_symbols
        ["Outlines a C# file: its types and their members (methods, properties, fields, events) with signatures and positions. Paged."] =
            "Outline C# file: types + members (methods, properties, fields, events) with signatures + positions. Paged.",
        ["Path to a C# source file in the project."] =
            "Path to C# source file in project.",
        ["Optional nesting depth: 0 for the top-level types only, 1 to add their members and nested types, and so on; without it, everything."] =
            "Opt. nesting depth: 0 = top-level types only, 1 = + members and nested types, etc.; omit = everything.",

        // list_symbols
        ["Lists the symbols declared in a symbol: a namespace's namespaces and types, a type's members, a method's or indexer's parameters and locals. For a variable, field or other property it lists the members of its type. Paged."] =
            "List symbols declared in symbol: namespace → namespaces + types, type → members, method/indexer → parameters + locals. Variable, field or other property → members of its type. Paged.",
        ["Symbol path: a dotted name, optionally partly qualified, e.g. 'OrderService', 'OrderService.Submit(Order)' for one overload, 'OrderService.Submit.order' for a parameter or local, 'OrderService..ctor', 'Type.this[int]'. A result's Container + '.' + Name is a path."] =
            "Symbol path: dotted name, opt. partly qualified, e.g. 'OrderService.Submit(Order)' = one overload, 'OrderService.Submit.order' = parameter/local, 'OrderService..ctor', 'Type.this[int]'. Result's Container + '.' + Name = path.",

        // find_symbols
        ["Searches symbols (namespaces, types, members, parameters, locals) by name and structure (attribute, base type, type, accessibility, modifiers) in the whole solution or within a scope. Outer symbols come first. Paged."] =
            "Search symbols (namespaces, types, members, parameters, locals) by name + structure (attribute, base type, type, accessibility, modifiers), whole solution or within scope. Outer symbols first. Paged.",
        ["Case-insensitive .NET regular expression matched against symbol names, e.g. 'cache', '^Get.*Async$' or 'path|file'. A query with a dot also matches full names that end in the symbol's name, e.g. 'Tools.ResultCache' or 'MyNamespace.Outer.Nested'. Omit to return everything in scope."] =
            "Case-insensitive .NET regex on symbol names, e.g. '^Get.*Async$' or 'path|file'. Query with dot also matches full names ending in symbol's name, e.g. 'Tools.ResultCache'. Omit = everything in scope.",
        ["Optional path of a C# file, or a symbol path to search within. Symbol path: a dotted name, optionally partly qualified, e.g. 'OrderService', 'OrderService.Submit(Order)' for one overload, 'OrderService.Submit.order' for a parameter or local, 'OrderService..ctor', 'Type.this[int]'. A result's Container + '.' + Name is a path."] =
            "Opt. C# file path or symbol path to search within. Symbol path: dotted name, opt. partly qualified, e.g. 'OrderService.Submit(Order)' = one overload, 'OrderService.Submit.order' = parameter/local, 'OrderService..ctor', 'Type.this[int]'. Result's Container + '.' + Name = path.",
        ["Kind of symbols to return. Variable means locals and parameters."] =
            "Symbol kind to return. Variable = locals + parameters.",
        ["Optional regex for the name of an attribute the symbol has, with or without 'Attribute', e.g. 'Obsolete' or '^(Test|Fact)$'."] =
            "Opt. regex for name of attribute symbol has, with/without 'Attribute', e.g. 'Obsolete' or '^(Test|Fact)$'.",
        ["Optional regex for a base type or interface of the type, e.g. 'IDisposable' or 'ControllerBase'."] =
            "Opt. regex for base type or interface of type, e.g. 'IDisposable' or 'ControllerBase'.",
        ["Optional regex for the type of a variable, field, property or event, or the return type of a method, e.g. '^Task' or 'List<int>'."] =
            "Opt. regex for type of variable/field/property/event, or method return type, e.g. '^Task' or 'List<int>'.",
        ["Optional accessibility: Public, Internal, Protected, Private, ProtectedOrInternal or ProtectedAndInternal."] =
            "Opt. accessibility: Public, Internal, Protected, Private, ProtectedOrInternal or ProtectedAndInternal.",
        ["Optional modifiers that must all apply: static, abstract, virtual, override, sealed, extern, async, readonly, const, extension or generic."] =
            "Opt. modifiers, all must apply: static, abstract, virtual, override, sealed, extern, async, readonly, const, extension or generic.",

        // definition
        ["Resolves a symbol: its signature, documentation and declaration locations (go to definition + hover). For a symbol from a referenced assembly, AssemblyPath is the file to pass to assembly_il, assembly_asm or decompile; and if its PDB leads to source, Locations point into local copies of the files that Sources describe."] =
            "Resolve symbol: signature, docs, declaration locations (go to definition + hover). Symbol from referenced assembly: AssemblyPath = file to pass to assembly_il, assembly_asm or decompile; if its PDB leads to source, Locations point into local copies of files Sources describe.",
        ["The symbol, by path; or leave this out and give filePath, line and column. Symbol path: a dotted name, optionally partly qualified, e.g. 'OrderService', 'OrderService.Submit(Order)' for one overload, 'OrderService.Submit.order' for a parameter or local, 'OrderService..ctor', 'Type.this[int]'. A result's Container + '.' + Name is a path."] =
            "Symbol by path; or omit + give filePath, line, column. Symbol path: dotted name, opt. partly qualified, e.g. 'OrderService.Submit(Order)' = one overload, 'OrderService.Submit.order' = parameter/local, 'OrderService..ctor', 'Type.this[int]'. Result's Container + '.' + Name = path.",
        ["With line and column, instead of a symbol path: the C# file that contains the symbol."] =
            "With line + column, instead of symbol path: C# file containing symbol.",
        ["1-based line number."] =
            "1-based line number.",
        ["1-based column number of the symbol."] =
            "1-based column of symbol.",
        ["Resolve the symbol's type instead (go to type definition): the type of a variable, parameter, field, property or event, or the return type of a method, or for a constructor its type; for an array its element type."] =
            "Resolve symbol's type instead (go to type definition): type of variable/parameter/field/property/event, method return type, constructor's type; array → element type.",

        // references
        ["Finds all references to a symbol across the project or solution, with the member that contains each reference and whether it reads, writes or calls the symbol. Paged."] =
            "Find all references to symbol across project/solution, with containing member of each + whether it reads, writes or calls symbol. Paged.",

        // symbol_source
        ["Returns the source code of a symbol's declaration, such as a whole method, property or type; one entry per declaration, so a partial type has several. Each Source starts at the beginning of the line given by its Location. For a symbol from a referenced assembly, the source comes from the files its PDB lists (see assembly_sources), and Document tells where from and whether it is verified. That source lacks what the compiler synthesizes or IL weavers add, which decompile shows."] =
            "Source of symbol's declaration (whole method, property, type); one entry per declaration, so partial type has several. Each Source starts at start of line its Location gives. Symbol from referenced assembly: source from files its PDB lists (see assembly_sources); Document tells where from + whether verified. That source lacks compiler-synthesized code + IL weaver additions; decompile shows those.",
        ["For a symbol declared in source: also the last this many commits that changed its lines, with git log -L, each with its author, date, message and the diff of those lines: why the code is as it is, or which commit brought in a change."] =
            "Symbol declared in source: also return this many last commits changing its lines (git log -L), each with author, date, message + diff of those lines: why code is so, or which commit introduced change.",

        // implementations
        ["Finds what implements or overrides a symbol: the types implementing an interface, the members implementing an interface member, and the overrides of an abstract or virtual member. Paged."] =
            "Find what implements/overrides symbol: types implementing interface, members implementing interface member, overrides of abstract/virtual member. Paged.",

        // type_hierarchy
        ["Lists the supertypes (base types, then all implemented interfaces) or the subtypes (all derived types, and for an interface its derived interfaces and implementing types) of a type. For a variable, field or property, of its type; for a constructor, of the type it constructs. Paged."] =
            "List supertypes (base types, then all implemented interfaces) or subtypes (all derived types; for interface, derived interfaces + implementing types) of type. Variable/field/property → its type; constructor → type it constructs. Paged.",
        ["Supertypes or Subtypes."] =
            "Supertypes or Subtypes.",

        // callers
        ["Finds the members that call or use a symbol (incoming calls), each with the locations of its calls. Calls through an interface or base member are included. Paged."] =
            "Find members calling/using symbol (incoming calls), each with call locations. Includes calls via interface or base member. Paged.",

        // callees
        ["Finds what a member uses (outgoing calls): the methods, constructors, properties, events, method groups and user-defined operators it refers to, each with the locations it refers to them at. Paged."] =
            "Find what member uses (outgoing calls): methods, constructors, properties, events, method groups, user-defined operators it refers to, each with locations. Paged.",

        // expression_info
        ["Describes the expression at a position and up to 4 expressions around it, innermost first: its type, the type it converts to and how, its constant value, its null state and the symbol it binds to; when binding fails, the candidates and why they failed."] =
            "Describe expression at position + up to 4 around it, innermost first: type, converted type + conversion kind, constant value, null state, bound symbol; binding fails → candidates + why failed.",
        ["1-based column within the expression."] =
            "1-based column within expression.",

        // data_flow
        ["Analyzes the statements between two lines, which must be in one block, as for extracting a method: the variables declared, flowing in and out, read and written inside and outside, and captured by lambdas; and the exits (return, break, continue, goto) and whether the end can be reached."] =
            "Analyze statements between two lines (must be in one block), as for extract method: variables declared, flowing in/out, read/written inside/outside, captured by lambdas; exits (return, break, continue, goto) + whether end reachable.",
        ["1-based first line."] =
            "1-based first line.",
        ["1-based last line."] =
            "1-based last line.",

        // unused_symbols
        ["Finds types and members declared in source that nothing references, not even the code under #if that the load leaves out; the members of an unused type are left out, and so is generated code. Public ones are left out unless asked for, as they can be used from outside. So are overrides, interface implementations, constructors, entry points and their types, and symbols with attributes, as frameworks, serializers or reflection may use those. Paged."] =
            "Find unused source-declared types/members: nothing references them, not even code under #if the load leaves out. Excluded: members of unused type, generated code; public ones unless asked (usable from outside); overrides, interface implementations, constructors, entry points + their types, symbols with attributes (frameworks/serializers/reflection may use them). Paged.",
        ["Optional path of a C# file, or a symbol path, to search within. Symbol path: a dotted name, optionally partly qualified, e.g. 'OrderService', 'OrderService.Submit(Order)' for one overload, 'OrderService.Submit.order' for a parameter or local, 'OrderService..ctor', 'Type.this[int]'. A result's Container + '.' + Name is a path."] =
            "Opt. C# file path or symbol path to search within. Symbol path: dotted name, opt. partly qualified, e.g. 'OrderService.Submit(Order)' = one overload, 'OrderService.Submit.order' = parameter/local, 'OrderService..ctor', 'Type.this[int]'. Result's Container + '.' + Name = path.",
        ["Include symbols that are visible outside the assembly too."] =
            "Include symbols visible outside assembly too.",

        // generated_sources
        ["Lists the files that source generators add to the projects, such as for System.Text.Json, regular expressions or logging; with a name, returns the content of the matching files. They are part of the compilation but not on disk. Paged."] =
            "List files source generators add to projects (e.g. System.Text.Json, regex, logging); with name → content of matching files. Part of compilation, not on disk. Paged.",
        ["Optional hint name, or part of it, of the files to return the content of, e.g. 'RegexGenerator' or 'MyContext.g.cs'."] =
            "Opt. hint name or part of it, of files to return content of, e.g. 'RegexGenerator' or 'MyContext.g.cs'.",

        // document_highlight
        ["Finds where the symbol at a position occurs in that same file, in order: its declarations, reads, writes and calls."] =
            "Find where symbol at position occurs in same file, in order: declarations, reads, writes, calls.",

        // signature_help
        ["At a position between the parentheses (or brackets) of a call, constructor or indexer: the overloads it could call, with their documentation, which one the arguments pick, and which parameter the argument at the position goes to."] =
            "Position inside parentheses/brackets of call, constructor or indexer: callable overloads with docs, which one arguments pick, which parameter argument at position goes to.",
        ["1-based column within the argument list."] =
            "1-based column within argument list.",

        // completion
        ["Lists what can be written at a position, as an IDE's completion list does: the members after a dot, or the locals, parameters, members, types and keywords in scope there. A filter keeps the list short. Paged."] =
            "List what can be written at position, like IDE completion: members after dot, or locals, parameters, members, types, keywords in scope. Filter keeps list short. Paged.",
        ["1-based column where the text would go, such as just after a dot."] =
            "1-based column where text would go, e.g. just after dot.",
        ["Optional: only items whose name starts with this, ignoring case."] =
            "Opt.: only items whose name starts with this, case-insensitive.",

        // inlay_hints
        ["Returns lines of a file with the hints an IDE shows inline, as /*...*/ comments: the type inferred for var and for lambda parameters, and the parameter names of literal and new arguments. Each line starts with its number."] =
            "Return file lines with IDE inline hints as /*...*/ comments: inferred type for var + lambda parameters, parameter names of literal and new arguments. Each line starts with its number.",

        // syntax_tree
        ["Dumps the syntax tree of some lines of a file, from the smallest node that covers them: each node with its kind, line and text, indented by depth. With operations, dumps the semantic operation tree instead, with types, constants and the symbols operations refer to. For writing analyzers or generators, or understanding how code binds. At most 400 nodes."] =
            "Dump syntax tree of some file lines, from smallest node covering them: each node with kind, line, text, indented by depth. With operations → semantic operation tree instead, with types, constants, referenced symbols. For writing analyzers/generators or understanding binding. Max 400 nodes.",
        ["Dump operations instead of syntax."] =
            "Dump operations instead of syntax.",

        // code_metrics
        ["Measures code, most complex first. Members: the cyclomatic complexity, lines, parameters and deepest nesting of each method, constructor, operator, or property or event with code. Types: their member count, lines, total and highest member complexity, and class coupling: how many other types they use. In the whole solution, a file or a symbol. Generated code, such as source generators' output and files marked auto-generated, is left out unless asked for, and so are the generated parts of partial types. Paged."] =
            "Measure code, most complex first. Members: cyclomatic complexity, lines, parameters, deepest nesting per method, constructor, operator, or property/event with code. Types: member count, lines, total + highest member complexity, class coupling (count of other types used). Whole solution, file or symbol. Generated code (source generator output, files marked auto-generated) + generated parts of partial types excluded unless asked. Paged.",
        ["Optional path of a C# file, or a symbol path, to measure within. Symbol path: a dotted name, optionally partly qualified, e.g. 'OrderService', 'OrderService.Submit(Order)' for one overload, 'OrderService.Submit.order' for a parameter or local, 'OrderService..ctor', 'Type.this[int]'. A result's Container + '.' + Name is a path."] =
            "Opt. C# file path or symbol path to measure within. Symbol path: dotted name, opt. partly qualified, e.g. 'OrderService.Submit(Order)' = one overload, 'OrderService.Submit.order' = parameter/local, 'OrderService..ctor', 'Type.this[int]'. Result's Container + '.' + Name = path.",
        ["Members or Types."] =
            "Members or Types.",
        ["Measure generated code too, and count the generated parts of partial types."] =
            "Measure generated code too, + count generated parts of partial types.",

        // search_pattern
        ["Searches the text of the solution's C# files, including generated ones that are not on disk, with a regular expression; within a file, a project or a symbol's declaration if asked. Returns each match, once per line it starts on, with its lines and context lines, numbered. Paged."] =
            "Regex search of solution's C# file text, incl. generated files not on disk; opt. within file, project or symbol's declaration. Returns each match once per start line, with its lines + context lines, numbered. Paged.",
        ["A .NET regular expression; ^ and $ match at line ends, and it can span lines, e.g. 'async void' or 'catch\\s*\\{\\s*\\}'."] =
            ".NET regex; ^ and $ match at line ends; may span lines, e.g. 'async void' or 'catch\\s*\\{\\s*\\}'.",
        ["Optional scope: a C# file, a project name, or a symbol path to search only its declaration; without it, every file of the solution, generated ones included. Symbol path: a dotted name, optionally partly qualified, e.g. 'OrderService', 'OrderService.Submit(Order)' for one overload, 'OrderService.Submit.order' for a parameter or local, 'OrderService..ctor', 'Type.this[int]'. A result's Container + '.' + Name is a path."] =
            "Opt. scope: C# file, project name, or symbol path (search only its declaration); omit = every solution file, generated included. Symbol path: dotted name, opt. partly qualified, e.g. 'OrderService.Submit(Order)' = one overload, 'OrderService.Submit.order' = parameter/local, 'OrderService..ctor', 'Type.this[int]'. Result's Container + '.' + Name = path.",
        ["How many lines of context to show before and after each match."] =
            "Context lines before + after each match.",
        ["Match case."] =
            "Match case.",

        // find_operations
        ["Finds code by what it does rather than by name: the operations of a kind, such as Invocation, ObjectCreation, Throw, Await, Lock, Using, Conversion, Literal or Coalesce, and optionally only those whose target matches a regex: the method called, the type created or thrown, the type converted to, or else the operation's type. Compiler-generated ones, such as implicit conversions, count too. Paged."] =
            "Find code by what it does, not name: operations of kind, e.g. Invocation, ObjectCreation, Throw, Await, Lock, Using, Conversion, Literal, Coalesce; opt. only those whose target matches regex: method called, type created/thrown, type converted to, else operation's type. Compiler-generated ones (e.g. implicit conversions) count too. Paged.",
        ["The kind of operation, a name of Roslyn's OperationKind, e.g. 'Invocation' or 'ObjectCreation'."] =
            "Operation kind, a Roslyn OperationKind name, e.g. 'Invocation' or 'ObjectCreation'.",
        ["Optional case-insensitive regex for the target, e.g. 'HttpClient' or 'Task\\.Result'."] =
            "Opt. case-insensitive regex for target, e.g. 'HttpClient' or 'Task\\.Result'.",

        // evaluate
        ["Runs C# script code, such as an expression or a few statements, in a separate process that references the projects' current code and their dependencies; returns what it wrote to the console and the value of its last expression."] =
            "Run C# script code (expression or few statements) in separate process referencing projects' current code + dependencies; returns console output + value of last expression.",
        ["C# script code. A last expression without a semicolon is the value, e.g. 'new MyApp.Parser().Parse(\"1+2\")'. System, System.Collections.Generic, System.IO, System.Linq, System.Text and System.Threading.Tasks are imported."] =
            "C# script code. Last expression without semicolon = value, e.g. 'new MyApp.Parser().Parse(\"1+2\")'. Imported: System, System.Collections.Generic, System.IO, System.Linq, System.Text, System.Threading.Tasks.",
        ["How long it may run before it is stopped."] =
            "Max run time before stopped.",

        // dotnet_processes
        ["Lists the running .NET processes, to inspect with process_threads or process_heap: their id, name, start time and executable. With processId (or dumpPath), only that one, with how it was started and what it loaded: its command line, environment variables, runtime version and assemblies with their versions and paths, for a FileLoadException or a version conflict, or to check that it runs the build it should."] =
            "Lists running .NET processes (id, name, start time, executable) for process_threads/process_heap. With processId or dumpPath: that one only + command line, environment variables, runtime version, assemblies with versions + paths. For FileLoadException, version conflict, wrong build running.",
        ["The id of one process to describe in detail, e.g. from this list."] =
            "Process id to describe in detail, e.g. from this list.",
        ["Or else a dump file to describe: its runtime and assemblies."] =
            "Or else: dump file to describe (runtime + assemblies).",
        ["Optional regex for the paths of the assemblies to list, e.g. 'Newtonsoft|MyApp'."] =
            "Opt. regex on assembly paths, e.g. 'Newtonsoft|MyApp'.",

        // process_threads
        ["Snapshots a running .NET process and returns its managed threads, each with its call stack, the exception it is handling, if any, and the locks it holds that other threads wait for: for hangs, deadlocks and errors in progress; an async method that waits has no thread, so process_async shows those. Frames give their source file and line where a PDB is at hand, also one that assembly_sources or symbol_source downloaded earlier. The process pauses only while the snapshot is taken. Reads a dump file instead, such as one of a crash or a hang."] =
            "Snapshot of running .NET process: managed threads with call stack, exception being handled, locks held that others wait on. For hangs, deadlocks, errors in progress. Awaiting async methods have no thread → process_async. Frames get file + line where PDB on hand (incl. assembly_sources/symbol_source downloads). Pauses process only during snapshot. Or reads dump file (crash, hang).",
        ["The id of a running process, e.g. from dotnet_processes; or else pass dumpPath."] =
            "Running process id, e.g. from dotnet_processes; or else pass dumpPath.",
        ["The path of a dump file to read instead of a running process, such as one from dotnet-dump collect or a crash."] =
            "Dump file to read instead of live process, e.g. from dotnet-dump collect or crash.",
        ["How many frames to return per thread, from the innermost."] =
            "Frames per thread, from innermost.",
        ["Also find the System.Threading.Lock objects each thread holds, which takes a walk of the heap; Monitor locks (C# lock on an object) show without it."] =
            "Also find System.Threading.Lock objects held per thread (walks heap); Monitor locks (C# lock) show without it.",

        // process_heap
        ["Snapshots a running .NET process and summarizes its managed heap: the object count and size, and the types that take the most memory with their object counts and sizes: for memory leaks and growth. With compareTo, the Snapshot of an earlier call, the types that grew the most since, with how much, which points at a leak. Walking a big heap takes a while. Reads a dump file instead, if it has the heap."] =
            "Snapshot of running .NET process managed heap: object count + size, top types by memory with counts + sizes. For memory leaks/growth. compareTo = earlier Snapshot → types that grew most, by how much (leak hint). Big heaps slow. Or reads dump file, if it has heap.",
        ["Optional regex for the type names to show, e.g. '^MyApp\\.' or 'Cache'."] =
            "Opt. regex on type names, e.g. '^MyApp\\.' or 'Cache'.",
        ["How many types to return, largest first, or that grew most first with compareTo."] =
            "Types to return, largest first (most grown with compareTo).",
        ["The Snapshot of an earlier process_heap result to compare with."] =
            "Snapshot from earlier process_heap result to compare with.",

        // process_objects
        ["Shows objects on the managed heap of a running .NET process or a dump, with their field values: the object at an address, or else the first objects of a type, such as the ones process_heap counts. A field that references an object shows its address, to follow with another call. Also says which thread holds the object's lock, if one does."] =
            "Shows managed heap objects of running .NET process or dump with field values: object at address, or else first objects of type (as process_heap counts). Reference fields show address → follow with another call. Also says which thread holds object's lock.",
        ["The address of an object, such as 0x1f2e3d4c5b6a from a field or a root chain."] =
            "Object address, e.g. 0x1f2e3d4c5b6a from field or root chain.",
        ["Or else a regex for the type names of the objects to show, e.g. '^MyApp\\.OrderCache$'."] =
            "Or else regex on type names, e.g. '^MyApp\\.OrderCache$'.",
        ["How many objects of the type to show."] =
            "Max objects of type to show.",

        // process_roots
        ["Explains why an object in a running .NET process or a dump stays alive, as gcroot does: the chains of references from roots, such as locals, static fields and handles, to the object. For memory leaks, after process_heap and process_objects. Slow on big heaps. Finding locals needs the code the JIT compiled, which only a full dump has: in other dumps, chains from locals can be missing."] =
            "Why object in running .NET process or dump stays alive, like gcroot: reference chains from roots (locals, static fields, handles). For memory leaks, after process_heap + process_objects. Slow on big heaps. Local roots need JIT code, only in full dump; else may miss them.",
        ["The address of the object, e.g. from process_objects."] =
            "Object address, e.g. from process_objects.",
        ["How many chains to return."] =
            "Max chains to return.",

        // process_cpu
        ["Profiles where a running .NET process spends its time, as dotnet-trace's CPU sampling does: samples managed thread stacks for a while and returns the threads by how busy they were, and the methods that run most, with their own share of the samples and their share including what they call. Filters pick the threads that run a method and leave out samples, such as of waits; root gives the call tree under a method, leaf the call paths into one. Each result has a Profile, to analyze the same samples again with other filters, or to compare a later profile with, as before and after a change. For slow code and high CPU; symbol_asm shows the code of a hot method. The process keeps running. With launch, it starts a program or a test project instead and profiles it from its start, for code that ends before it could be attached to, such as a slow test. With trace, it reads a .nettrace recorded earlier instead, such as by dotnet-trace collect on CI or another machine."] =
            "CPU profile of running .NET process, like dotnet-trace sampling: samples managed stacks for a while → threads by busyness, top methods with own + inclusive share. Filters pick threads running a method, drop samples (e.g. waits); root = call tree under method, leaf = call paths into it. Result has Profile: re-analyze with other filters, or compare later profile (before/after). For slow code, high CPU; symbol_asm shows hot method code. Process keeps running. launch = start program/test project, profile from start (e.g. slow test). trace = read earlier .nettrace (e.g. CI).",
        ["The process id, e.g. from dotnet_processes; or else pass launch, trace or profile."] =
            "Process id, e.g. from dotnet_processes; or else pass launch, trace or profile.",
        ["Instead of processId: a .dll to start, or a project to build and start, whose runtime waits at startup until the trace begins, so it covers the program from its first instruction, startup included, until it exits or seconds pass, when it is ended. For a test project, its tests run (testFilter picks them) and the trace is of the process that runs them."] =
            "Instead of processId: .dll to start, or project to build + start. Runtime waits until trace begins → covers startup until exit or seconds pass (then killed). Test project: runs its tests (testFilter), traces test process.",
        ["With launch: the program's command-line arguments, or more arguments for the test application."] =
            "With launch: program args, or extra test application args.",
        ["With launch of a test project: the tests to run, as run_tests' filter takes them."] =
            "With launch of test project: tests to run, run_tests filter syntax.",
        ["Instead of processId or launch: a .nettrace file recorded earlier, such as by dotnet-trace collect on CI or another machine, to read instead of tracing; seconds does not apply. It has only the events its providers recorded: dotnet-trace's default profile has the CPU samples, exceptions, contention and collections, its gc-verbose profile the allocations, and logs and requests need the Microsoft-Extensions-Logging and System.Diagnostics.Metrics providers."] =
            "Instead of processId/launch: earlier .nettrace (e.g. dotnet-trace collect on CI) to read; seconds ignored. Only recorded events: default profile = CPU samples, exceptions, contention, collections; gc-verbose = allocations; logs + requests need Microsoft-Extensions-Logging + System.Diagnostics.Metrics providers.",
        ["How long to sample; the process should be busy with the work to look at meanwhile. At most this long: it ends when the process exits."] =
            "Sample duration; keep process busy with target work. Ends early if process exits.",
        ["The Profile of an earlier result, to analyze its samples again instead of sampling."] =
            "Profile of earlier result, to re-analyze instead of sampling.",
        ["The Profile of an earlier result to compare with: methods come in order of how much their share changed."] =
            "Profile of earlier result to compare: methods ordered by share change.",
        ["Optional regex: counts only the threads that ran a method whose name matches, e.g. 'WorldLoop\\.OnDay'."] =
            "Opt. regex: only threads that ran matching method, e.g. 'WorldLoop\\.OnDay'.",
        ["Optional regex: leaves out the samples with a method that matches, such as waiting for a lock or for a GC, e.g. 'Monitor\\.Wait|WaitOneNoCheck|PollGC'."] =
            "Opt. regex: drop samples with matching method (lock/GC waits), e.g. 'Monitor\\.Wait|WaitOneNoCheck|PollGC'.",
        ["Optional regex: also returns the call trees under the methods that match, e.g. 'Reactive\\.Propagate'."] =
            "Opt. regex: also call trees under matching methods, e.g. 'Reactive\\.Propagate'.",
        ["Optional regex: also returns the call paths into the methods that match, out to their callers, e.g. 'Lock\\.TryEnterSlow'."] =
            "Opt. regex: also call paths into matching methods, out to callers, e.g. 'Lock\\.TryEnterSlow'.",
        ["How many methods to return, those that run themselves the most first."] =
            "Methods to return, highest own share first.",

        // process_allocations
        ["Profiles what a running .NET process allocates, as dotnet-trace's gc-verbose profile does: for a while, samples allocations with the runtime's allocation ticks, one about every 100 KB, and estimates the bytes by thread, by type and by the stack that allocated them; and lists the types allocated that have a finalizer, which cost more than their bytes. For GC pressure: process_counters gives the allocation rate and GC counts, process_heap what stays alive. The process keeps running. With launch, it starts a program or a test project instead and profiles it from its start; finalizers then go unnoticed. With trace, it reads a .nettrace recorded earlier instead, as dotnet-trace collect --profile gc-verbose writes."] =
            "Allocation profile of running .NET process, like dotnet-trace gc-verbose: samples allocation ticks (~1 per 100 KB) for a while, estimates bytes by thread, type, stack; lists allocated types with finalizer (cost beyond bytes). For GC pressure; process_counters = allocation rate + GC counts, process_heap = what stays alive. Process keeps running. launch = start program/test project, profile from start (finalizers missed). trace = read earlier .nettrace (dotnet-trace collect --profile gc-verbose).",
        ["The process id, e.g. from dotnet_processes; or else pass launch."] =
            "Process id, e.g. from dotnet_processes; or else pass launch.",
        ["Optional regex: counts only the threads that allocated from a method whose name matches, e.g. 'WorldLoop\\.OnDay'."] =
            "Opt. regex: only threads allocating from matching method, e.g. 'WorldLoop\\.OnDay'.",
        ["How many types and stacks to return, those that allocate the most first."] =
            "Types + stacks to return, most allocating first.",
        ["How many frames to return per stack, from the innermost."] =
            "Frames per stack, from innermost.",

        // process_calls
        ["Runs a built program for a while and counts how often methods of the libraries it uses are called, per thread: in all, per second, and per call of a marker method, such as one that runs a frame or a game day. Where process_cpu estimates time, this counts calls exactly; each counted call costs a few nanoseconds more. It runs copies of the libraries' build output with a counter added to each method, so build first; the build stays as it is, but for a deps.json beside the program while it runs. Methods of the program's own assembly cannot be counted."] =
            "Runs built program for a while, counts exact calls to methods of its libraries, per thread: total, per second, per call of marker method (e.g. frame or game day). process_cpu estimates time; this counts calls, ~few ns per call. Runs instrumented copies of libraries' build output → build first; build untouched, but deps.json beside program while running. Program's own assembly not countable.",
        ["The program's .dll in its build output, or its .exe beside it, e.g. 'src/Game/bin/Release/net10.0/Game.dll'."] =
            "Program's .dll in build output, or .exe beside it, e.g. 'src/Game/bin/Release/net10.0/Game.dll'.",
        ["The methods to count, by symbol path: a property counts its accessors, a type all its methods. Symbol path: a dotted name, optionally partly qualified, e.g. 'OrderService', 'OrderService.Submit(Order)' for one overload, 'OrderService.Submit.order' for a parameter or local, 'OrderService..ctor', 'Type.this[int]'. A result's Container + '.' + Name is a path."] =
            "Methods to count, by symbol path: property = its accessors, type = all methods. Symbol path: dotted, opt. partly qualified, e.g. 'OrderService.Submit(Order)' (overload), 'OrderService.Submit.order' (parameter/local), 'OrderService..ctor', 'Type.this[int]'. Result Container + '.' + Name = path.",
        ["Optional: a method to count the others per call of, by symbol path, such as the one that runs a frame."] =
            "Opt.: marker method (symbol path) to count others per call of, e.g. frame loop.",
        ["The program's command-line arguments."] =
            "Program args.",
        ["The directory to run the program in; by default the program's own."] =
            "Working directory; default = program's own.",
        ["How long to run the program for; it is stopped then, unless it exited before."] =
            "Run duration; then stopped, unless exited.",

        // process_counters
        ["Reads the runtime counters of a running .NET process, as dotnet-counters does: CPU usage, working set, GC heap size and collection counts, allocation rate, thread pool threads and queue length, lock contention, exceptions per second and more."] =
            "Runtime counters of running .NET process, like dotnet-counters: CPU usage, working set, GC heap size + collection counts, allocation rate, thread pool threads + queue length, lock contention, exceptions/sec, more.",
        ["The process id, e.g. from dotnet_processes."] =
            "Process id, e.g. from dotnet_processes.",
        ["How long to listen; the counters report once a second."] =
            "Listen duration; counters report once/sec.",

        // debug_start
        ["Starts debugging a program with netcoredbg: builds a project (or takes a .dll), sets breakpoints, runs it, and waits until it stops at a breakpoint, an unhandled exception or its entry, or exits. Returns its state: why it stopped, the call stack, the locals of the top frame, and its output so far. Ends an earlier session. Runs the program for real."] =
            "Debug program with netcoredbg: builds project (or takes .dll), sets breakpoints, runs, waits until stop (breakpoint, unhandled exception, entry) or exit. Returns state: stop reason, call stack, top frame locals, output so far. Ends earlier session. Runs program for real.",
        ["The project (.csproj) to build and run, or a .dll to run."] =
            "Project (.csproj) to build + run, or .dll.",
        ["Command-line arguments for the program."] =
            "Program args.",
        ["Breakpoints: at the 1-based Line of the file at FilePath, or where the method Function starts, such as 'MyApp.Orders.Submit'; each with an optional Condition such as 'i == 3'."] =
            "Breakpoints: 1-based Line in FilePath, or start of method Function (e.g. 'MyApp.Orders.Submit'); opt. Condition, e.g. 'i == 3'.",
        ["With Line: the file to break in."] =
            "With Line: file to break in.",
        ["The 1-based line to break at."] =
            "1-based line.",
        ["Break only when this C# expression is true, such as 'i == 3'."] =
            "Break only when C# expression true, e.g. 'i == 3'.",
        ["Or else: the method to break where it starts, such as 'MyApp.Orders.Submit' or 'Submit'."] =
            "Or else: method to break at start of, e.g. 'MyApp.Orders.Submit' or 'Submit'.",
        ["Makes it a logpoint, which does not stop the program: each time it is hit, this message is recorded with the C# expressions in braces evaluated, such as 'i={i}, total={order.Total}', and the program goes on. The messages come with the next state, in Logged."] =
            "Logpoint, no stop: each hit records message with C# expressions in braces evaluated, e.g. 'i={i}, total={order.Total}'. Messages in next state's Logged.",
        ["Stop at the first line of Main."] =
            "Stop at first line of Main.",
        ["Exception types to stop at where they are thrown, even when caught, such as 'System.InvalidOperationException'; '*' for every exception. Unhandled exceptions always stop."] =
            "Exception types to stop at when thrown, even if caught, e.g. 'System.InvalidOperationException'; '*' = all. Unhandled always stop.",
        ["How long to wait for the program to stop or exit; it keeps running after that, and the state says Running."] =
            "Wait for stop/exit; after that keeps running, state = Running.",

        // debug_attach
        ["Attaches the debugger to a running .NET process, such as a service that misbehaves, sets breakpoints, and waits until it stops or exits. Returns its state as debug_start does. debug_stop detaches and leaves the process running. Ends an earlier session."] =
            "Attach debugger to running .NET process (e.g. misbehaving service), set breakpoints, wait until stop/exit. Returns state like debug_start. debug_stop detaches, process keeps running. Ends earlier session.",

        // debug_test
        ["Debugs tests: builds and runs them with dotnet test, attaches the debugger to the test host, sets breakpoints, and waits until it stops or exits. Returns its state as debug_start does; once the test host exits, Output has the test results. Ends an earlier session; debug_stop ends the test run."] =
            "Debug tests: builds + runs dotnet test, attaches to test host, sets breakpoints, waits until stop/exit. Returns state like debug_start; after test host exits, Output has results. Ends earlier session; debug_stop ends test run.",
        ["The test project (.csproj)."] =
            "Test project (.csproj).",
        ["A dotnet test --filter expression for the tests to run, such as a Filter from find_tests; omit to run all of them."] =
            "dotnet test --filter expression, e.g. Filter from find_tests; omit = all tests.",
        ["How long to wait for the program to stop or exit; it keeps running after that, and the state says Running. The build comes first."] =
            "Wait for stop/exit, after build; then keeps running, state = Running.",

        // debug_set
        ["Sets a variable, field or property of the stopped program to the value of a C# expression, such as order.Total to 0, to try a fix or a case before resuming. Returns the new value."] =
            "Set variable, field or property of stopped program to C# expression value (e.g. order.Total to 0), to try fix/case before resuming. Returns new value.",
        ["What to set, such as 'total' or 'order.Total'."] =
            "Target, e.g. 'total' or 'order.Total'.",
        ["The new value, as a C# expression such as '0', '\"text\"' or 'other.Total'."] =
            "New value, C# expression e.g. '0', '\"text\"', 'other.Total'.",
        ["The frame, by its index in the stack: 0 is the innermost."] =
            "Frame index in stack: 0 = innermost.",

        // debug_breakpoints
        ["Replaces the breakpoints in one file of the program being debugged, or else all the breakpoints at methods; none removes them. Returns whether the debugger could bind each one to code."] =
            "Replaces breakpoints in one file of debugged program, or else all method breakpoints; none = remove them. Returns whether debugger bound each to code.",
        ["The file whose breakpoints to replace."] =
            "File whose breakpoints to replace.",
        ["1-based lines of the file to break at."] =
            "1-based lines in file to break at.",
        ["Or else: the methods to break at, such as 'MyApp.Orders.Submit'; replaces all the breakpoints at methods."] =
            "Or else: methods to break at, e.g. 'MyApp.Orders.Submit'; replaces all method breakpoints.",
        ["Optional conditions, one per line or method in the same order; null or empty for none."] =
            "Opt. conditions, one per line/method, same order; null/empty = none.",

        // debug_resume
        ["Resumes the program being debugged: Continue to the next breakpoint, StepOver, StepInto or StepOut; or Pause it while it runs. Waits until it stops or exits, and returns its state as debug_start does. Continue on a running program just waits again."] =
            "Resumes debugged program: Continue to next breakpoint, StepOver, StepInto, StepOut; or Pause while running. Waits until stop/exit, returns state as debug_start does. Continue on running program just waits again.",
        ["Continue, StepOver, StepInto, StepOut or Pause."] =
            "Continue, StepOver, StepInto, StepOut or Pause.",

        // debug_inspect
        ["Inspects the stopped program: evaluates an expression in a frame, such as 'order.Total * 2' or 'items.Count'; or lists what a variable contains, by the Reference of a variable from an earlier result; or else lists the locals of a frame."] =
            "Inspects stopped program: evaluate expression in frame, e.g. 'order.Total * 2'; or list variable contents by Reference of variable from earlier result; or else list frame locals.",
        ["Optional C# expression to evaluate."] =
            "Opt. C# expression to evaluate.",
        ["Optional Reference of a variable, to list its fields, properties or items."] =
            "Opt. Reference of variable → list its fields, properties or items.",

        // debug_stop
        ["Stops debugging: ends the program and the debugger."] =
            "Stops debugging: ends program + debugger.",

        // rename_symbol
        ["Renames a symbol declared in source, with all its references across the solution, and the file of a type that has its name. Writes the changed files to disk and returns their paths, and the lines under #if that the load leaves out and that have the old name, which it does not change."] =
            "Renames symbol declared in source + all references across solution, + file of type named after it. Writes changed files to disk, returns paths, + lines under #if excluded from load that have old name (not changed).",
        ["The new name."] =
            "New name.",
        ["For a method, rename its overloads too."] =
            "Method: rename overloads too.",

        // code_actions
        ["Lists the code fixes for the diagnostics at a position, line or selection and the refactorings available there, like an IDE's light bulb: add a using, implement an interface, generate a method, extract a method from the selected statements, introduce a local for the selected expression, fix a warning, and so on. Apply one with apply_code_action. Refactorings that need an IDE dialog are left out; change_signature and extract_interface run two of them. Paged."] =
            "Lists code fixes for diagnostics at position/line/selection + refactorings there, like IDE light bulb: add using, implement interface, generate method, extract method from selected statements, introduce local for selected expression, fix warning, etc. Apply with apply_code_action. Refactorings needing IDE dialog excluded; change_signature + extract_interface run two of them. Paged.",
        ["Optional 1-based column; without it, the whole line counts."] =
            "Opt. 1-based column; omitted = whole line.",
        ["Optional 1-based last line of a selection, such as the statements to extract into a method."] =
            "Opt. 1-based last line of selection, e.g. statements to extract into method.",
        ["Optional 1-based column where the selection ends, exclusive; without it, the selection runs to the end of endLine."] =
            "Opt. 1-based column where selection ends, exclusive; omitted = to end of endLine.",

        // apply_code_action
        ["Applies a code action that code_actions listed, by its id; writes the changed files to disk and returns their paths. Ids hold until the code changes. An action whose title ends in '...' picks members, as an IDE would ask for in a dialog."] =
            "Applies code action listed by code_actions, by id; writes changed files to disk, returns paths. Ids valid until code changes. Action title ending '...' picks members, as IDE dialog would ask.",
        ["The Id of an entry from code_actions."] =
            "Id of entry from code_actions.",
        ["For an action that picks members, such as 'Generate overrides...' or 'Generate constructor from members...': the members to use, by name; all it offers by default."] =
            "Member-picking action (e.g. 'Generate overrides...', 'Generate constructor from members...'): members to use, by name; default all offered.",

        // fix_all
        ["Fixes every diagnostic with an id at once, in one file or in the whole solution, as an IDE's 'Fix all occurrences' does; writes the changed files to disk and returns their paths."] =
            "Fixes every diagnostic with id at once, in one file or whole solution, like IDE 'Fix all occurrences'; writes changed files to disk, returns paths.",
        ["The diagnostic id, such as CS0168 or IDE0005."] =
            "Diagnostic id, e.g. CS0168 or IDE0005.",
        ["Optional C# file to fix in; without it, the whole solution."] =
            "Opt. C# file to fix in; omitted = whole solution.",
        ["Which fix to apply, by its title as code_actions lists it, when there are several; the first otherwise."] =
            "Which fix to apply, by title as code_actions lists it, when several; default first.",

        // format
        ["Formats a C# file, or some of its lines, by the project's .editorconfig settings, and writes it to disk."] =
            "Formats C# file or some lines per project .editorconfig, writes to disk.",
        ["Optional 1-based first line to format; without it, the whole file."] =
            "Opt. 1-based first line to format; omitted = whole file.",
        ["Optional 1-based last line to format; without it, only startLine."] =
            "Opt. 1-based last line to format; omitted = only startLine.",

        // organize_usings
        ["Removes the using directives a C# file does not need, sorts the rest as the project's .editorconfig says, and writes it to disk."] =
            "Removes unneeded using directives from C# file, sorts rest per project .editorconfig, writes to disk.",
        ["Remove unneeded usings; otherwise only sort."] =
            "Remove unneeded usings; else only sort.",

        // replace_symbol_body
        ["Replaces a symbol's whole declaration, such as a method with its signature, with new code; symbol_source returns the code this replaces, from the start of its first line. Writes the file to disk."] =
            "Replaces symbol's whole declaration (e.g. method + signature) with new code; symbol_source returns code replaced, from start of its first line. Writes file to disk.",
        ["The new declaration, indented as it should be."] =
            "New declaration, indented as it should be.",

        // insert_before_symbol
        ["Inserts code on new lines before a symbol's declaration and its documentation comment, such as a new member or a using. Writes the file to disk."] =
            "Inserts code on new lines before symbol's declaration + its doc comment, e.g. new member or using. Writes file to disk.",
        ["The code to insert, indented as it should be."] =
            "Code to insert, indented as it should be.",

        // insert_after_symbol
        ["Inserts code on new lines after a symbol's declaration, such as a new member after an existing one. Writes the file to disk."] =
            "Inserts code on new lines after symbol's declaration, e.g. new member after existing one. Writes file to disk.",

        // safe_delete_symbol
        ["Deletes a symbol's declaration, but only if nothing else references it; otherwise deletes nothing and returns the references, or the code that would no longer compile without it, such as the uses of an implicit conversion, or the lines under #if that the load leaves out and that have its name. Writes the changed files to disk, and deletes a file that declared nothing but a deleted type."] =
            "Deletes symbol's declaration only if nothing references it; else deletes nothing, returns references, or code that would stop compiling without it (e.g. uses of implicit conversion), or lines under #if excluded from load that have its name. Writes changed files to disk; deletes file that declared only deleted type.",

        // preview_edit
        ["Shows what edits would break before making them: applies text replacements in memory only, and returns the errors and warnings they would introduce and resolve, in the edited projects and in the projects that depend on them. Nothing is written."] =
            "Shows what edits would break before making them: applies text replacements in memory only, returns errors + warnings introduced and resolved, in edited projects + projects depending on them. Nothing written.",
        ["The replacements, applied in order: in the file at filePath, oldText becomes newText."] =
            "Replacements, applied in order: in file at filePath, oldText → newText.",
        ["The C# file to edit."] =
            "C# file to edit.",
        ["Text that occurs exactly once in the file."] =
            "Text occurring exactly once in file.",
        ["The text to put in its place."] =
            "Text to put in its place.",
        ["Whether to run the project's analyzers too; slower."] =
            "Also run project analyzers; slower.",

        // build
        ["Builds a project or solution with dotnet build, and returns whether it succeeded, with its errors and warnings (errors first, at most 100). Output is the end of the build log when it failed without any. With timings, also where the build spent its time. With binlogPath, keeps the build's binary log for the build_* tools."] =
            "Builds project/solution with dotnet build, returns success + errors and warnings (errors first, max 100). Output = end of build log when failed without any. timings → also where build spent time. binlogPath → keeps binary log for build_* tools.",
        ["The configuration, such as Debug or Release."] =
            "Configuration, e.g. Debug or Release.",
        ["Also time the build from its binary log: how long each project, target and task took, the analyzers and source generators of the compilations that ran, the evaluations and the slowest places in them, and the chain of projects that set the build's length. An up-to-date build skips most of the work, compilations included."] =
            "Also time build from binary log: duration per project, target, task; analyzers + source generators of compilations run; evaluations + slowest spots; project chain setting build length. Up-to-date build skips most work, compilations included.",
        ["Where to write the build's binary log (.binlog), to read it with build_problems, build_target_why and the other build_* tools, or in the MSBuild Structured Log Viewer."] =
            "Where to write build binary log (.binlog), for build_problems, build_target_why, other build_* tools, or MSBuild Structured Log Viewer.",

        // build_problems
        ["Checks a build's binary log for problems that make outputs wrong, parallel builds flaky or builds slow: files that copies from more than one source wrote (double writes: the last copy wins), projects built more than once with different global properties, and assembly version conflicts, with what depends on each version and which one ResolveAssemblyReference chose."] =
            "Checks build binary log for problems → wrong outputs, flaky parallel builds, slow builds: files written by copies from >1 source (double writes: last copy wins), projects built more than once with different global properties, assembly version conflicts with dependents of each version + which ResolveAssemblyReference chose.",
        ["Path to a binary log (.binlog): one that build wrote with binlogPath, or one from dotnet build -bl elsewhere, such as CI."] =
            "Path to binary log (.binlog): one build wrote with binlogPath, or from dotnet build -bl elsewhere, e.g. CI.",

        // build_incrementality
        ["Checks that a build is incremental: builds the project twice and reports what the second build, which should have nothing to do, did anyway: the targets with Inputs and Outputs that ran, each with the output that was missing or the input that was newer, and the files it copied. Or reads the binary log of such a second build. Empty lists mean the build is incremental."] =
            "Checks build is incremental: builds project twice, reports what second build (should do nothing) did anyway: targets with Inputs/Outputs that ran, each with missing output or newer input, + files copied. Or reads binary log of such second build. Empty lists = incremental.",
        ["The project or solution to build twice; or leave this out and give binlogPath."] =
            "Project/solution to build twice; or omit and give binlogPath.",
        ["Instead of projectPath: the binary log of a build that ran right after another one."] =
            "Instead of projectPath: binary log of build run right after another.",

        // build_file_origin
        ["Explains where a file that a build wrote came from: each copy to it, with its source, project, target and task; then where each source came from in turn, back to the compilation that wrote it. Copies from different sources to one file are a double write: the last one wins. Empty if no copy or compilation in the log wrote it; other tasks are not tracked."] =
            "Explains origin of file a build wrote: each copy to it with source, project, target, task; then each source's origin in turn, back to compilation that wrote it. Copies from different sources to one file = double write: last wins. Empty if no copy/compilation in log wrote it; other tasks not tracked.",
        ["The file, such as a DLL in a bin directory."] =
            "File, e.g. DLL in bin directory.",

        // build_target_why
        ["Explains why a target ran or was skipped, in each project of a build's binary log where it appears: the chain of targets that asked for it, through DependsOnTargets, BeforeTargets or AfterTargets, up to the target the build started with; and for a target with Inputs and Outputs, the output that was missing or the input that was newer, or that its outputs were up to date. For a target that ran after another (AfterTargets), the log does not name that other target, only the target that asked for it, so the chain goes on from there. A target that is not in the log never ran: nothing asked for it, or its condition was false."] =
            "Explains why target ran or was skipped, in each project of build binary log where it appears: chain of targets requesting it via DependsOnTargets, BeforeTargets or AfterTargets, up to build's start target; for target with Inputs/Outputs, missing output or newer input, or outputs up to date. Target run after another (AfterTargets): log names only requester, not that other target, so chain continues from there. Target not in log never ran: nothing asked for it, or condition false.",
        ["The target, such as CoreCompile."] =
            "Target, e.g. CoreCompile.",
        ["Only projects whose file name contains this, such as App.csproj."] =
            "Only projects whose file name contains this, e.g. App.csproj.",

        // build_property_trace
        ["Traces an MSBuild property or item type through a build's binary log: its value in each project after evaluation; then each change in order: reassignments during evaluation with file and line, and while targets ran, properties set in a target or output by a task, and items added, removed or output by a task, each with project, target and task. Unlike msbuild_property_trace, which reads the project, this shows what happened in that build. At most 300 changes."] =
            "Traces MSBuild property or item type through build binary log: value per project after evaluation; then each change in order: reassignments during evaluation with file + line; while targets ran, properties set in target or output by task, items added/removed/output by task, each with project, target, task. Unlike msbuild_property_trace (reads project), shows what happened in that build. Max 300 changes.",
        ["The property or item type, such as DefineConstants or Compile."] =
            "Property or item type, e.g. DefineConstants or Compile.",

        // build_log_search
        ["Searches a build's binary log as the MSBuild Structured Log Viewer does, and returns each match with the project, target and task it is in. Words match the text of any node: messages, properties, items, metadata, targets and tasks; \"quotes\" match a phrase. A $kind keeps one kind of node, such as '$task Csc', '$target CoreCompile', '$project App' or '$property name=LangVersion'; project(App) keeps the matches in that project, and under($project App) those in it and in the projects it built."] =
            "Searches build binary log like MSBuild Structured Log Viewer, returns each match with its project, target, task. Words match text of any node: messages, properties, items, metadata, targets, tasks; \"quotes\" = phrase. $kind keeps one node kind, e.g. '$task Csc', '$property name=LangVersion'; project(App) keeps matches in that project, under($project App) those in it + projects it built.",
        ["The query, such as '$task Csc project(App)'."] =
            "Query, e.g. '$task Csc project(App)'.",
        ["At most this many matches."] =
            "Max matches.",

        // build_diff
        ["Compares two binary logs of a build, such as a passing and a failing one, or a local and a CI one: the environment variables that differ; per project, the global properties, the properties and items after evaluation, and the compiler arguments (where both compiled) that differ; the projects in only one log; and the projects whose work took the most more or less time. Projects match by their path relative to the directory that holds a log's projects, which values show as <root>, so logs from different machines compare."] =
            "Compares two build binary logs (e.g. passing vs failing, local vs CI): differing env vars; per project, differing global properties, evaluated properties + items, compiler arguments (where both compiled); projects only in one log; projects whose work took most more/less time. Projects match by path relative to directory holding log's projects (shown as <root>), so logs from different machines compare.",
        ["The other binary log."] =
            "Other binary log.",

        // find_tests
        ["Finds NUnit, xUnit, MSTest and TUnit test methods: all of them, or those that use a symbol (for a type, any of its members), directly or through up to 3 calls in between. Each comes with its project and a Filter for run_tests. Paged."] =
            "Finds NUnit, xUnit, MSTest, TUnit test methods: all, or those using symbol (type → any member), directly or via up to 3 intermediate calls. Each with project + Filter for run_tests. Paged.",
        ["Optional: only tests that use this symbol. The symbol, by path; or leave this out and give filePath, line and column. Symbol path: a dotted name, optionally partly qualified, e.g. 'OrderService', 'OrderService.Submit(Order)' for one overload, 'OrderService.Submit.order' for a parameter or local, 'OrderService..ctor', 'Type.this[int]'. A result's Container + '.' + Name is a path."] =
            "Opt.: only tests using this symbol. Symbol by path; or omit and give filePath, line, column. Symbol path: dotted name, opt. partly qualified, e.g. 'OrderService', 'OrderService.Submit(Order)' for one overload, 'OrderService.Submit.order' for parameter/local, 'OrderService..ctor', 'Type.this[int]'. Result's Container + '.' + Name = path.",

        // affected_tests
        ["Finds the tests a change can affect: maps the lines of C# files that differ from a git commit (uncommitted changes by default, new untracked files included) to the members they are in, then finds the tests that use those members, directly or through up to 3 calls in between, as find_tests does. Returns the changed members, the tests, and one Filter for run_tests that runs them all; and the members added and removed since that commit, as for reviewing a branch or writing its description."] =
            "Finds tests a change can affect: maps lines of C# files differing from git commit (default uncommitted changes, new untracked files included) to containing members, then finds tests using those members, directly or via up to 3 intermediate calls, like find_tests. Returns changed members, tests, one Filter for run_tests running all; + members added/removed since that commit, e.g. for reviewing branch or writing its description.",
        ["The git commit, branch or tag to compare the working tree with, such as HEAD, main or HEAD~3."] =
            "Git commit/branch/tag to diff working tree against, e.g. HEAD, main, HEAD~3.",

        // public_api
        ["Lists the public API of a project, as other assemblies see it: its public and protected types, with their base types and interfaces, and members, with their full signatures, one line each and sorted. To compare it with an earlier version, use api_diff. Paged."] =
            "Public API of project as other assemblies see it: public/protected types w/ bases, interfaces, members w/ full signatures; one line each, sorted. Compare versions → api_diff. Paged.",
        ["For a solution, the name of the project; a .csproj path picks itself."] =
            "Solution: project name; .csproj path = itself.",

        // api_diff
        ["Compares the public API of a project's current source with an earlier build of it, such as the last released version from package_assembly or a build output: the lines of public_api that were removed or changed, which can break callers, and those that were added. A changed signature shows as one removed and one added line."] =
            "Diff public API of current source vs earlier build (e.g. release from package_assembly, build output): public_api lines removed/changed (may break callers) + added. Changed signature = 1 removed + 1 added line.",
        ["The path of the earlier assembly (.dll) to compare with."] =
            "Earlier assembly (.dll) path to compare.",

        // namespace_graph
        ["Maps which namespaces of the solution's source use which others, as an architecture view: each dependency with how many types of one use types of the other, and examples; and the cycles, groups of namespaces that depend on each other, which break layering. project_graph shows the same between projects."] =
            "Namespace dependency map of solution source (architecture view): each dependency w/ count of using types + examples; cycles (mutually dependent namespace groups, break layering). project_graph = same per project.",
        ["Optional number of leading name parts to group namespaces by, such as 2 to count MyApp.Orders.Domain as MyApp.Orders."] =
            "Opt. leading name parts to group by, e.g. 2: MyApp.Orders.Domain → MyApp.Orders.",
        ["Optional namespace prefix to keep, such as 'MyApp.Orders'; dependencies on other namespaces of the solution still show."] =
            "Opt. namespace prefix to keep, e.g. 'MyApp.Orders'; deps on other solution namespaces still shown.",

        // change_signature
        ["Changes the parameters of a method, constructor, indexer or delegate and updates every call site, override, implementation and documentation comment across the solution, as an IDE's Change signature does: reorders, removes and adds parameters. Writes the changed files to disk and returns their paths."] =
            "Change signature of method/constructor/indexer/delegate (IDE Change signature): reorder, remove, add params; updates all call sites, overrides, implementations, doc comments in solution. Writes files to disk, returns paths.",
        ["The new parameter list in order: existing parameters by Name alone, so leaving one out removes it; new ones with Name, Type and a Value for the call sites to pass, or a DefaultValue to make them optional."] =
            "New param list in order: existing by Name only (omitted → removed); new w/ Name, Type + Value passed at call sites, or DefaultValue → optional.",
        ["An existing parameter's name, or a new one's."] =
            "Name of existing or new param.",
        ["For a new parameter: its type, such as 'int' or 'List<string>'."] =
            "New param type, e.g. 'int', 'List<string>'.",
        ["For a new parameter: the expression call sites pass, such as '0' or 'null'."] =
            "New param: expression call sites pass, e.g. '0', 'null'.",
        ["For a new parameter: makes it optional with this default; call sites then pass nothing unless Value is given."] =
            "New param: default → optional; call sites pass nothing unless Value set.",

        // extract_interface
        ["Extracts an interface from a class, struct or interface: declares it with the chosen public instance members, in a new file by default, and makes the type implement it. Writes the changed files to disk and returns their paths."] =
            "Extract interface from class/struct/interface: declares it w/ chosen public instance members, new file by default; type implements it. Writes files to disk, returns paths.",
        ["The interface's name; I plus the type's name by default."] =
            "Interface name; default I + type name.",
        ["The members to include, by name; all public instance members by default. A static one becomes static abstract."] =
            "Members to include by name; default all public instance. Static → static abstract.",
        ["Declare the interface in the type's file rather than in a new one."] =
            "Declare interface in type's file, not new one.",

        // move_to_namespace
        ["Moves a type, or all the types of a namespace declaration, to another namespace, as an IDE's Move to namespace does: changes the declaration and updates the using directives of the code that uses them. Writes the changed files to disk and returns their paths."] =
            "Move type, or all types of namespace declaration, to other namespace (IDE Move to namespace); updates using directives of users. Writes files to disk, returns paths.",
        ["The namespace to move to, such as 'MyApp.Orders'."] =
            "Target namespace, e.g. 'MyApp.Orders'.",

        // run_tests
        ["Builds and runs tests with dotnet test, and returns the counts and the failures with their messages and stack traces (at most 50). Works with VSTest and, where global.json opts into it, Microsoft.Testing.Platform. When the build fails, BuildErrors says why. Output is the end of the log when there is nothing else to go on. With coverage, also the lines of each source file that no test ran. With hangTimeout, a test that hangs ends the run with a dump and the test host's threads; with repeat, the tests run several times, to tell flaky ones; with mutate, Stryker.NET changes the code of those files and reports the changes no test noticed."] =
            "Build + run tests via dotnet test → counts, failures w/ messages + stack traces (max 50). VSTest, or Microsoft.Testing.Platform where global.json opts in. Build fail → BuildErrors. Else nothing → log tail in Output. coverage → unrun lines per source file. hangTimeout → hung test ends run w/ dump + test host threads. repeat → multiple runs to find flaky tests. mutate → Stryker.NET mutates those files, reports mutations no test caught.",
        ["A dotnet test --filter expression, such as a Filter from find_tests, 'FullyQualifiedName~Namespace.Class' or 'TestCategory=Fast'. Under Microsoft.Testing.Platform, xUnit v3 and TUnit take only test names this way, as find_tests gives them. Omit to run all tests."] =
            "dotnet test --filter expr, e.g. Filter from find_tests, 'FullyQualifiedName~Namespace.Class', 'TestCategory=Fast'. Microsoft.Testing.Platform xUnit v3/TUnit: test names only, as find_tests gives. Omit → all tests.",
        ["Also measure code coverage: per source file, how many of its lines ran and which did not. Slower. Under Microsoft.Testing.Platform it needs the Microsoft.Testing.Extensions.CodeCoverage package, which MSTest.Sdk has."] =
            "Also code coverage: lines run/unrun per source file. Slower. Microsoft.Testing.Platform needs Microsoft.Testing.Extensions.CodeCoverage package (in MSTest.Sdk).",
        ["More arguments for dotnet test, such as --filter-trait for xUnit v3 or --treenode-filter for TUnit."] =
            "Extra dotnet test args, e.g. --filter-trait (xUnit v3), --treenode-filter (TUnit).",
        ["Seconds a test may run before the run counts as hung: the test host is then dumped and ended, and Hang gives the test that ran, the dump (for process_threads, process_async or process_heap) and the test host's threads. Under Microsoft.Testing.Platform it needs the Microsoft.Testing.Extensions.HangDump package."] =
            "Seconds before test run counts as hung → test host dumped + killed; Hang = running test, dump (for process_threads/process_async/process_heap), host threads. Microsoft.Testing.Platform needs Microsoft.Testing.Extensions.HangDump package.",
        ["How many times to run the tests, to tell flaky ones: FailedRuns then says in how many runs each test failed, with its distinct messages. Runs after the first do not build."] =
            "Run count, to find flaky tests: FailedRuns = per test failed-run count + distinct messages. Runs after first skip build.",
        ["C# source files to mutation test, of one project the test project references: Stryker.NET (installed on first use) changes their code in small ways, such as > to >=, runs the tests against each change, and Mutation lists the changes that no test failed on. VSTest projects; slow, minutes per file. Filter and the other options do not apply."] =
            "C# files to mutation test, from one project test project references: Stryker.NET (installed first use) mutates code (e.g. > → >=), runs tests per mutation; Mutation = survivors no test failed on. VSTest projects; slow, minutes/file. Filter + other options ignored.",

        // msbuild_evaluate
        ["Returns MSBuild properties and items per project, as its design-time build evaluated them, such as TargetPath, DefineConstants, Compile or PackageReference. Names may contain * wildcards."] =
            "MSBuild properties/items per project from design-time evaluation, e.g. TargetPath, DefineConstants, Compile, PackageReference. * wildcards OK.",
        ["Property or item names, e.g. 'TargetPath', '*Version*' or 'PackageReference'."] =
            "Property/item names, e.g. 'TargetPath', '*Version*', 'PackageReference'.",

        // project_graph
        ["Lists the loaded projects in build order, each with its target framework, output type, the projects it references and the projects that reference it."] =
            "Loaded projects in build order: target framework, output type, references, referenced-by.",

        // workspace_options
        ["Reloads a project or solution for another target framework or configuration, such as net48 or Release, so that code under #if for it shows, with its diagnostics; every later call with this path uses them. Omit both to go back to the defaults: each project's first target framework, and its own configuration. Returns each project with the framework it loaded and the ones it has."] =
            "Reload project/solution for other target framework or configuration (e.g. net48, Release) → code under #if shows w/ diagnostics; later calls w/ this path keep it. Omit both → defaults (first target framework, own configuration). Returns per project loaded + available frameworks.",
        ["The target framework, such as net48 or net10.0, for the projects that have it; the others keep their first."] =
            "Target framework, e.g. net48, net10.0, for projects having it; others keep first.",
        ["The configuration, such as Release."] =
            "Configuration, e.g. Release.",

        // msbuild_property_trace
        ["Explains where an MSBuild property gets its value: every place in the project and its imports, such as Directory.Build.props and the SDK's props and targets, that sets it, in evaluation order, each with its file, condition and the text it sets; and the value it ends with. Settings inside a target apply only when that target runs. Global properties and environment variables can set it too."] =
            "Trace MSBuild property value: every setter in project + imports (Directory.Build.props, SDK props/targets) in evaluation order w/ file, condition, text; + final value. Settings in target apply only if target runs. Global properties/env vars may also set it.",
        ["Path to a .csproj file."] =
            "Path to .csproj.",
        ["The property, such as LangVersion or OutputPath."] =
            "Property, e.g. LangVersion, OutputPath.",

        // packages
        ["Lists the NuGet packages of the projects with dotnet list package: all of them, or only the outdated ones (with their latest version), the vulnerable ones (with severities and advisories) or the deprecated ones; transitive packages too, if asked. Outdated, Vulnerable and Deprecated ask the package sources. Paged."] =
            "NuGet packages via dotnet list package: all, or Outdated (+ latest), Vulnerable (+ severities, advisories), Deprecated; transitive opt. Outdated/Vulnerable/Deprecated query sources. Paged.",
        ["All, Outdated, Vulnerable or Deprecated."] =
            "All, Outdated, Vulnerable or Deprecated.",
        ["Include transitive packages."] =
            "Include transitive packages.",

        // package_search
        ["Searches the project's NuGet package sources, as its nuget.config sets them, for packages by keywords or id. Returns each package's latest version, description, authors, total downloads and whether its id prefix is verified. Paged."] =
            "Search NuGet sources from nuget.config by keywords or id → latest version, description, authors, total downloads, id prefix verified. Paged.",
        ["Search terms, e.g. 'json serializer', or a package id."] =
            "Search terms, e.g. 'json serializer', or package id.",
        ["Include prerelease versions."] =
            "Include prerelease.",

        // package_info
        ["Describes a NuGet package from the project's package sources without downloading it: its versions, newest first, and for one version its description, license, deprecation, known vulnerabilities, the target frameworks it has dependencies for, the nearest of those for each of the project's target frameworks, and its dependencies there."] =
            "NuGet package info from sources, no download: versions newest first; per version description, license, deprecation, vulnerabilities, dependency target frameworks, nearest per project framework + dependencies there.",
        ["Package id, e.g. 'Newtonsoft.Json'."] =
            "Package id, e.g. 'Newtonsoft.Json'.",
        ["Package version, e.g. '13.0.3'; the latest stable one by default."] =
            "Version, e.g. '13.0.3'; default latest stable.",
        ["Include prerelease versions, also in picking the latest."] =
            "Include prerelease, also for latest.",
        ["Target framework to pick for, e.g. 'net8.0'; by default those of the project, or of the solution's projects."] =
            "Target framework, e.g. 'net8.0'; default project's or solution projects'.",

        // package_assembly
        ["Downloads a NuGet package into the global packages folder, where restore finds it later, and returns the paths of its assemblies for each of the project's target frameworks, and of its readme. decompile, assembly_sources, assembly_il and assembly_asm take those paths, so a package's API can be read before adding it. Its documentation comments, if any, sit next to each assembly in a .xml file. Symbols tells per assembly whether it has a PDB (looking on the symbol servers too) and SourceLink, so its original source can be read."] =
            "Download NuGet package to global packages folder (restore reuses) → assembly paths per project target framework + readme. Paths feed decompile, assembly_sources, assembly_il, assembly_asm: read API before adding. Doc comments in .xml beside assembly if any. Symbols: per assembly PDB (symbol servers too) + SourceLink → original source.",

        // package_upgrade_check
        ["Checks what moving NuGet packages to other versions would break, without changing anything: compiles the projects that reference them against the other versions' assemblies and returns the compiler errors and warnings that would be new, such as uses of removed or obsolete APIs. Dependencies are not resolved: a version that needs a newer version of another package shows that as an error such as CS1705, so include that package too. Paged."] =
            "Upgrade check, no changes: compile referencing projects against other package versions' assemblies → new compiler errors/warnings (removed/obsolete APIs). Deps not resolved: needed newer dep shows as e.g. CS1705 → include it too. Paged.",
        ["Packages as 'Id@Version', or just 'Id' for the latest stable version, e.g. ['Newtonsoft.Json@13.0.3']."] =
            "'Id@Version' or 'Id' (latest stable), e.g. ['Newtonsoft.Json@13.0.3'].",

        // package_why
        ["Explains why a NuGet package is in the project's dependency graph, from what restore resolved: each chain of dependencies from a direct dependency of the project down to the package, per target framework, with resolved versions; and restore's warnings and errors about it, such as downgrades (NU1605), version conflicts (NU1107) or vulnerabilities (NU1901-NU1904). Empty if nothing depends on it."] =
            "Why NuGet package in dependency graph, from restore: chains from direct dependency to package per target framework w/ resolved versions; + restore warnings/errors e.g. downgrade (NU1605), conflict (NU1107), vulnerability (NU1901-NU1904). Empty if unused.",
        ["Most dependency paths to return."] =
            "Max dependency paths.",

        // package_contents
        ["Downloads a NuGet package into the global packages folder and lists its files, with what reaches the build of a project that references it: the MSBuild .props and .targets files restore imports, per target framework of the project, and its analyzers and source generators. The files can be read from the package's directory."] =
            "Download NuGet package to global packages folder, list files + what reaches referencing build: .props/.targets restore imports per target framework, analyzers, source generators. Files readable in package dir.",
        ["Only list files in this folder of the package, e.g. 'build' or 'lib/net8.0'; packages can hold hundreds."] =
            "Only files in this package folder, e.g. 'build', 'lib/net8.0'; packages may hold hundreds.",

        // package_licenses
        ["Lists the license of every NuGet package the projects resolved, transitive ones included, from what restore wrote and the packages' own metadata in the packages folders, without network access. Paged."] =
            "License of every resolved NuGet package incl. transitive, from restore output + package metadata in packages folders; no network. Paged.",

        // nuget_config
        ["Returns the NuGet configuration restore uses for the project: the nuget.config files that apply, the package sources and whether they have credentials (never the credentials), package source mapping, the global packages folder and the fallback folders. Helps with restore errors such as NU1101 (package not found) or NU1301 (source unreachable)."] =
            "NuGet config restore uses: applicable nuget.config files, sources + has-credentials flag (never credentials), source mapping, global packages folder, fallback folders. For restore errors e.g. NU1101 (not found), NU1301 (unreachable).",

        // diagnostics
        ["Returns compiler and analyzer diagnostics (errors first) for the whole project/solution or a single file, with the severities the project's .editorconfig and build settings give them; for a project that did not load, such as one whose restore failed, the MSBuild errors that say why; and warnings where the server's compiler is older than the SDK's: options it does not know, such as a newer language version, and analyzers or generators it cannot load. With analyzerPackages, also the rules of analyzer packages the project does not reference, without changing it; with allRules, every rule, also those turned off; with aot, the trim, native AOT and single-file warnings (IL2xxx, IL3xxx) that publishing with PublishTrimmed or PublishAot would give. Paged."] =
            "Compiler + analyzer diagnostics (errors first) for project/solution or one file, severities per .editorconfig + build settings. Unloaded project (e.g. restore failed) → MSBuild errors why. Warns when server compiler older than SDK: unknown options (newer language version), unloadable analyzers/generators. analyzerPackages → rules of unreferenced analyzer packages, project unchanged; allRules → every rule incl. disabled; aot → trim/native AOT/single-file warnings (IL2xxx, IL3xxx) PublishTrimmed/PublishAot would give. Paged.",
        ["Optional path of a C# file to limit diagnostics to."] =
            "Opt. C# file path to limit to.",
        ["The least severity to return: Hidden, Info, Warning or Error."] =
            "Min severity: Hidden, Info, Warning or Error.",
        ["Whether to run the project's analyzers too, such as the .NET code style and quality analyzers; slower."] =
            "Also run project analyzers (e.g. .NET code style/quality); slower.",
        ["NuGet packages of analyzers to run as well, such as 'Roslynator.Analyzers', 'Meziantou.Analyzer' or 'Microsoft.CodeAnalysis.NetAnalyzers@10.0.100' (id, optionally @version, else the latest); downloaded into the global packages folder from the project's package sources. code_actions knows only the project's own analyzers."] =
            "Extra analyzer NuGet packages, e.g. 'Roslynator.Analyzers', 'Microsoft.CodeAnalysis.NetAnalyzers@10.0.100' (id, opt. @version, else latest); downloaded to global packages folder from project sources. code_actions knows project's own analyzers only.",
        ["Turns on every rule of the analyzers, also those off by default or turned off by .editorconfig, a global config or the SDK's analysis level, as warnings (or errors, if that is their default); combine with ids to keep the result short."] =
            "Enable every analyzer rule, incl. default-off or disabled by .editorconfig, global config, SDK analysis level, as warnings (errors if default); combine w/ ids to keep short.",
        ["Also runs the trim, native AOT and single-file analyzers, as IsAotCompatible turns them on, without changing the project: the code a trimmed or native AOT publish would break, such as reflection, Type.GetType by name and dynamic code. Downloads Microsoft.NET.ILLink.Tasks into the global packages folder."] =
            "Also run trim, native AOT, single-file analyzers (as IsAotCompatible), project unchanged: code trimmed/AOT publish breaks, e.g. reflection, Type.GetType by name, dynamic code. Downloads Microsoft.NET.ILLink.Tasks to global packages folder.",
        ["Optional regex for the diagnostic ids to return, e.g. '^CA18' or '^(RCS|MA)'."] =
            "Opt. regex for diagnostic ids, e.g. '^CA18', '^(RCS|MA)'.",

        // assembly_il
        ["Disassembles types or methods of any .NET assembly, such as a build output or a dependency, to IL. A type stands for all its methods. Every method comes with its compiler-generated helpers (async and iterator state machines, lambdas, local functions). Paged per method."] =
            "Disassemble types/methods of any .NET assembly (build output, dependency) to IL. Type = all methods. Includes compiler-generated helpers (async/iterator state machines, lambdas, local functions). Paged per method.",
        ["Path to the .dll or .exe."] =
            "Path to .dll or .exe.",
        ["Type or method name, optionally partially qualified, e.g. 'System.String.Concat', 'Dictionary.TryGetValue' or 'Outer.Nested'; or a documentation ID such as 'M:Ns.Type.Method(System.Int32)'. A method name returns all its overloads. A generic arity, as in 'List`1' or 'List<T>', must match; without one, any arity does."] =
            "Type/method name, opt. partially qualified, e.g. 'System.String.Concat', 'Outer.Nested'; or doc ID 'M:Ns.Type.Method(System.Int32)'. Method → all overloads. Generic arity ('List`1', 'List<T>') must match; none → any.",

        // symbol_il
        ["Compiles the declaring project from its current source and disassembles a type, method, property or event declared in it to IL. A type stands for all its methods. Every method comes with its compiler-generated helpers (async and iterator state machines, lambdas, local functions). Paged per method."] =
            "Compile declaring project from current source, disassemble declared type/method/property/event to IL. Type = all methods. Includes compiler-generated helpers (async/iterator state machines, lambdas, local functions). Paged per method.",

        // assembly_asm
        ["Disassembles the native code the JIT generates for types or methods of any .NET assembly, such as a build output or a dependency. The code comes from the JIT of the .NET the assembly is built for (its newest installed patch; for a framework assembly, its own framework), or else of the nearest installed .NET, fully optimized, for this machine's CPU; the listing names it. Assemblies built for debugging compile unoptimized (MinOpts). Generic code is shown for reference-type arguments, as the code they all share (System.__Canon). A type stands for all its methods. Every method comes with its compiler-generated helpers (async and iterator state machines, lambdas, local functions). Paged per method."] =
            "Disassembles JIT native code for types/methods of any .NET assembly (build output, dependency). JIT of assembly's target .NET (newest installed patch; framework assembly → own), else nearest installed; fully optimized, this CPU; listing names it. Debug builds → MinOpts. Generics shown for ref-type args as shared System.__Canon code. Type = all its methods. Includes compiler-generated helpers (async/iterator state machines, lambdas, local functions). Paged per method.",

        // symbol_asm
        ["Compiles the declaring project from its current source and disassembles the native code the JIT generates for a type, method, property or event declared in it. The code comes from the JIT of the .NET the assembly is built for (its newest installed patch; for a framework assembly, its own framework), or else of the nearest installed .NET, fully optimized, for this machine's CPU; the listing names it. Assemblies built for debugging compile unoptimized (MinOpts). Generic code is shown for reference-type arguments, as the code they all share (System.__Canon). A type stands for all its methods. Every method comes with its compiler-generated helpers (async and iterator state machines, lambdas, local functions). Paged per method. With typeArguments, generic code compiles for those instead, as each value type gets code of its own. With pgoWorkload, the code is what long-running code gets: Tier1, with Dynamic PGO's guarded devirtualization and block layout, and static readonly fields as constants."] =
            "Compiles declaring project from current source, disassembles JIT native code for type/method/property/event in it. JIT of assembly's target .NET (newest installed patch), else nearest installed; fully optimized, this CPU; listing names it. Debug builds → MinOpts. Generics shown for ref-type args as shared System.__Canon code. Type = all its methods. Includes compiler-generated helpers (state machines, lambdas, local functions). Paged per method. typeArguments → compile generics for those (value types get own code). pgoWorkload → long-running code: Tier1 + Dynamic PGO guarded devirtualization, block layout, static readonly as constants.",
        ["Type arguments for generic code, as code where the symbol is declared would write them, such as ['float'] or ['int', 'Vec2?']: the outermost type's parameters first, then a generic method's own. Those left out are reference types, which share one code."] =
            "Generic type args as written at symbol's declaration, e.g. ['float'] or ['int', 'Vec2?']: outermost type's first, then method's own. Omitted = ref types (shared code).",
        ["A parameterless method of the solution, by symbol path, such as a test or a benchmark, to run first: again and again, until the JIT has compiled what it calls often at Tier1 with what Dynamic PGO saw, for up to 20 seconds. Each listing is then of the last tier its method reached. A method that is not static runs on an instance from its type's parameterless constructor."] =
            "Parameterless solution method by symbol path (test, benchmark) run repeatedly first until JIT hits Tier1 w/ Dynamic PGO, max 20 seconds. Each listing = last tier reached. Instance method runs on instance from parameterless ctor.",

        // type_layout
        ["Shows where the runtime puts the fields of a class or struct declared in the solution, compiled from current source: each field's offset, as the JIT's code addresses it (symbol_asm), and size, base types' fields included; the padding between them; the size of an object or struct; and the 64-byte cache line each field starts in. Laid out by the .NET the project is built for, as symbol_asm compiles with, without running any of its code."] =
            "Field layout of solution class/struct, compiled from current source: per field offset (as symbol_asm code addresses it), size, base fields incl.; padding; object/struct size; 64-byte cache line each field starts in. Laid out by project's target .NET, no code run.",
        ["The class or struct, by symbol path. Symbol path: a dotted name, optionally partly qualified, e.g. 'OrderService', 'OrderService.Submit(Order)' for one overload, 'OrderService.Submit.order' for a parameter or local, 'OrderService..ctor', 'Type.this[int]'. A result's Container + '.' + Name is a path."] =
            "Class/struct, symbol path: dotted, opt. partly qualified, e.g. 'OrderService.Submit(Order)' (overload), 'OrderService.Submit.order' (param/local), 'OrderService..ctor', 'Type.this[int]'. Result Container + '.' + Name = path.",
        ["Type arguments for generic code, as code where the symbol is declared would write them, such as ['float'] or ['int', 'Vec2?']: the outermost type's parameters first, then a generic method's own. Those left out are System.Object, as all reference types lay out alike."] =
            "Generic type args as written at symbol's declaration, e.g. ['float'] or ['int', 'Vec2?']: outermost type's first, then method's own. Omitted = System.Object (ref types lay out alike).",

        // decompile
        ["Decompiles types or members of any .NET assembly to C# with ILSpy's decompiler, such as a dependency without source. definition gives the AssemblyPath of a symbol from a referenced assembly. A type comes out whole, with its nested types. This shows what the assembly really contains: code from source generators, members the compiler synthesizes and code IL weavers add. Local variables keep their names if a PDB is at hand. For the original source with comments, use assembly_sources or symbol_source. With lowered, it shows the C# the compiler lowered the code to instead: lambdas as closure classes, async and iterator methods as state machines (with their generated types), foreach, using, lock and string interpolation spelled out: where hidden allocations, boxing and captures come from. Paged."] =
            "Decompiles types/members of any .NET assembly to C# (ILSpy), e.g. dependency w/o source; definition gives AssemblyPath. Type whole + nested types. Shows real contents: source generator output, compiler-synthesized members, IL weaving. Locals named if PDB found. Original source w/ comments → assembly_sources/symbol_source. lowered → compiler-lowered C#: closures, async/iterator state machines, foreach/using/lock/interpolation expanded; reveals hidden allocations, boxing, captures. Paged.",
        ["Type or member name, optionally partially qualified, e.g. 'System.Linq.Enumerable', 'Dictionary.TryGetValue' or 'Outer.Nested'; or a documentation ID such as 'M:Ns.Type.Method(System.Int32)'. A method name returns all its overloads. A generic arity, as in 'List`1' or 'List<T>', must match; without one, any arity does."] =
            "Type/member name, opt. partly qualified, e.g. 'Dictionary.TryGetValue', or doc ID 'M:Ns.Type.Method(System.Int32)'. Method → all overloads. Generic arity ('List`1'/'List<T>') must match; none → any.",
        ["Show the lowered code, as the compiler emits it, with the compiler-generated types of a method's lambdas and state machines; build the project first to see its current source this way."] =
            "Show lowered code as compiler emits, w/ generated lambda/state machine types; build project first for current source.",

        // assembly_sources
        ["Lists the source files of any .NET assembly from its PDB, which comes from next to the assembly, from inside it, or from the Microsoft or NuGet symbol server. With a name or member, also returns their original source: embedded in the PDB, from disk, or through SourceLink from the repository at the commit it was built from; Verified tells whether a file has the checksum the PDB records. Unlike decompile, this keeps comments and original names, but lacks what the compiler synthesizes (records, async, lambdas) or IL weavers add; PDBs usually embed the code of source generators. Paged."] =
            "Lists source files of any .NET assembly from PDB (beside, embedded, or Microsoft/NuGet symbol server). name/member → also original source: PDB-embedded, disk, or SourceLink at build commit; Verified = PDB checksum matches. vs decompile: keeps comments/names, lacks synthesized code (records, async, lambdas) + IL weaving; generator code usually embedded. Paged.",
        ["Optional path, or part of it, of the files to return the source of, e.g. 'Parser.cs' or 'src/Json/'."] =
            "Opt. file path or part, e.g. 'Parser.cs' or 'src/Json/'.",
        ["Optional type or member whose files to return the source of, named as for decompile, e.g. 'JsonConvert' or 'JsonConvert.SerializeObject'."] =
            "Opt. type/member whose files to return, named as for decompile, e.g. 'JsonConvert.SerializeObject'.",

        // resolve_stack_trace
        ["Maps a .NET stack trace, such as from an exception's ToString(), a log, a test failure or CI output, to the solution's current source: per frame, the symbol path of the member it ran in, where compiler-generated frames name their method (an async or iterator MoveNext, a lambda's <>c__DisplayClass, a local function's <Method>g__Local), and its file and line with the text of that line. A frame without a line, as from a Release build without PDBs, points at the method's declaration; a line that is no longer in that member is flagged, as when the file changed since the build or the trace comes from a build machine. Frames of code outside the solution have no symbol."] =
            "Maps .NET stack trace (exception, log, test failure, CI) to current source: per frame member symbol path (compiler-generated frames → their method: MoveNext, <>c__DisplayClass, <Method>g__Local), file, line + text. No line (Release w/o PDB) → method declaration; line no longer in member flagged (stale build). Frames outside solution: no symbol.",
        ["The stack trace text: lines such as '   at MyApp.Orders.<SubmitAsync>d__4.MoveNext() in C:\\src\\Orders.cs:line 42'; other lines are skipped."] =
            "Stack trace text, lines like '   at MyApp.Orders.<SubmitAsync>d__4.MoveNext() in C:\\src\\Orders.cs:line 42'; others skipped.",

        // process_async
        ["Shows the async methods of a running .NET process or a dump that wait, as SOS's dumpasync does: chains of awaiting methods from the innermost out to the one that started them, each with the await it waits at (a line, where the PDB is at hand), and what the innermost waits for, such as a SemaphoreSlim, a TaskCompletionSource or a Task.Delay; chains of the same shape once, with their count. For hangs in async code, where process_threads shows idle threads, since an awaiting method has no thread. Walks the heap, so it takes a while on a big one."] =
            "Waiting async methods of running .NET process or dump (like SOS dumpasync): await chains innermost → starter, each w/ await line (if PDB), what innermost awaits (SemaphoreSlim, TaskCompletionSource, Task.Delay); same-shape chains once + count. For async hangs where process_threads shows idle threads. Walks heap: slow on big heaps.",
        ["Optional regex: only the chains with a method whose path matches, e.g. 'Orders\\.Submit'."] =
            "Opt. regex: chains w/ matching method path, e.g. 'Orders\\.Submit'.",
        ["How many chains to return, those that wait most often first."] =
            "Max chains, most frequent first.",

        // process_dump
        ["Writes a dump of a running .NET process to a file, as dotnet-dump collect does, to read with process_threads, process_async, process_heap, process_objects, process_roots and dotnet_processes after the process has moved on, exited or restarted, or to compare two moments. The process pauses while it is written. Returns the file's path and size."] =
            "Writes dump of running .NET process to file (like dotnet-dump collect) for process_threads, process_async, process_heap, process_objects, process_roots, dotnet_processes later, or to compare moments. Process pauses while writing. Returns path + size.",
        ["Mini (threads only), Heap (what the process tools read), Triage or Full (the whole memory, larger)."] =
            "Mini (threads only), Heap (what process tools read), Triage or Full (all memory, larger).",
        ["Where to write it; by default a new file under the temporary directory's Roslynaut/Dumps."] =
            "Output path; default new file in temp Roslynaut/Dumps.",

        // process_events
        ["Listens to a running .NET process, or a program or test project it starts with launch, for a while, and summarizes one kind of runtime event. Exceptions: those thrown, by type and the method that threw them, with a message, the stack and where they were caught, also those caught and logged nowhere. Logs: the Microsoft.Extensions.Logging messages from Debug up, without a restart (needs the EventSource logger, which the default hosts add). Contention: the time threads waited for locks, by the stack that waited. Collections: the garbage collections with their pauses and reasons, and who called GC.Collect. Requests: the HTTP requests served (by route) and sent (by host) with their p50, p95 and p99 durations, from .NET 8. The process keeps running. With trace, it reads a .nettrace recorded earlier instead, such as on CI."] =
            "Listens to running .NET process, or program/test project via launch, summarizes one runtime event kind. Exceptions: by type + thrower, message, stack, catch site, incl. swallowed. Logs: Microsoft.Extensions.Logging Debug+, no restart (needs EventSource logger, default hosts have it). Contention: lock wait time by stack. Collections: GCs w/ pauses, reasons, GC.Collect callers. Requests: HTTP served (by route) + sent (by host), p50/p95/p99, .NET 8+. Process keeps running. trace → read earlier .nettrace (e.g. CI).",
        ["Exceptions, Logs, Contention, Collections or Requests."] =
            "Exceptions, Logs, Contention, Collections or Requests.",
        ["How long to listen; the process should do the work to look at meanwhile. At most this long: it ends when the process exits."] =
            "Listen duration; process should do work meanwhile. Ends early if process exits.",
        ["Optional regex: the exception types or stacks, the log categories, the waiting stacks or the routes and hosts to keep, e.g. 'MyApp\\.'."] =
            "Opt. regex: exception types/stacks, log categories, wait stacks or routes/hosts to keep, e.g. 'MyApp\\.'.",
        ["For Logs: the least level, Debug, Information, Warning, Error or Critical."] =
            "Logs: min level Debug, Information, Warning, Error or Critical.",
        ["How many groups, messages, stacks or collections to return."] =
            "Max groups/messages/stacks/collections.",

        // call_paths
        ["Finds how one member reaches another through calls: the shortest call chains from 'from' to 'to', each hop with the line of its call, and the member it calls through where that is an interface or base member, as for 'how does this request reach the database' or 'can this code call that method'. Follows callers back from 'to', so calls through interfaces and overrides count; a lambda's calls count as its member's."] =
            "Shortest call chains 'from' → 'to', each hop w/ call line + interface/base member called through, e.g. 'how does request reach database'. Walks callers back from 'to': interface/override calls count; lambda calls count as its member's.",
        ["The member the chains start at, by symbol path. Symbol path: a dotted name, optionally partly qualified, e.g. 'OrderService', 'OrderService.Submit(Order)' for one overload, 'OrderService.Submit.order' for a parameter or local, 'OrderService..ctor', 'Type.this[int]'. A result's Container + '.' + Name is a path."] =
            "Chain start member, symbol path: dotted, opt. partly qualified, e.g. 'OrderService.Submit(Order)' (overload), 'OrderService.Submit.order' (param/local), 'OrderService..ctor', 'Type.this[int]'. Result Container + '.' + Name = path.",
        ["The member the chains end at, by symbol path."] =
            "Chain end member, symbol path.",
        ["The most calls a chain may have."] =
            "Max calls per chain.",
        ["How many chains to return, shortest first."] =
            "Max chains, shortest first.",

        // control_flow
        ["The control flow graph of a method, constructor or accessor, as the compiler builds it: its basic blocks with their lines, the condition each one branches on and where each outcome goes, where it returns or throws, which blocks cannot be reached, and the try, catch and finally each is in. With toLine, the conditions that hold on each path from the start to that line, as for what input reaches a throw, or which branch a test has to take to run a line coverage reports as not run. Lambdas and local functions inside are not followed."] =
            "Control flow graph of method/ctor/accessor: basic blocks w/ lines, branch conditions + targets, returns/throws, unreachable blocks, enclosing try/catch/finally. toLine → conditions on each path from start to line (what input reaches a throw, which branch covers an uncovered line). Nested lambdas/local functions not followed.",
        ["A 1-based line in the member: also the paths to it from the start, as the conditions along each."] =
            "1-based line in member: also start → line paths as conditions.",

        // throws
        ["Finds the exceptions that can leave a member: those it throws itself, those that members it calls throw (followed into source up to depth calls deep), and those that methods of referenced assemblies document with <exception>; each with where in the member it comes from and the chain of calls to the throw. Exceptions a catch on the way handles are left out. For error handling and tests, and what an API throws. It does not see exceptions the runtime raises, such as NullReferenceException, nor follow calls through interfaces or virtual members to their implementations."] =
            "Exceptions that can leave member: own throws, callees' (source followed to depth), referenced methods' <exception> docs; each w/ origin + call chain. Caught ones omitted. For error handling, tests, API throws. Misses runtime exceptions (NullReferenceException) + interface/virtual dispatch.",
        ["How many calls deep to follow members declared in source."] =
            "Call depth into source members.",

        // benchmark
        ["Measures how fast pieces of C# are with BenchmarkDotNet, in Release, against the project's current code: per case, the mean time with its error and standard deviation, the ratio to the first case, and the bytes allocated per operation with the Gen0 collections. Pass cases to compare implementations, as before keeping an optimization; or leave them out to run the project's own [Benchmark] classes, if it is a BenchmarkDotNet project. A short job takes about a minute per case; differences within the error are noise."] =
            "Benchmarks C# w/ BenchmarkDotNet, Release, project's current code: per case mean, error, std dev, ratio to first, bytes allocated/op, Gen0. cases → compare implementations; omit → run project's own [Benchmark] classes (BenchmarkDotNet project). Short ≈1 min/case; diffs within error = noise.",
        ["Path to a .csproj: the project whose code the cases use, or a BenchmarkDotNet project to run."] =
            ".csproj: project cases use, or BenchmarkDotNet project to run.",
        ["The cases by name, each C# code to measure: an expression whose value is kept, such as 'Parser.Parse(text)', or statements ending in ';'. The first is the baseline. They can use the project's code and what setup declares."] =
            "Named cases, C# to measure: expression (value kept), e.g. 'Parser.Parse(text)', or statements ending ';'. First = baseline. May use project code + setup.",
        ["C# members of the benchmark class that the cases use, set up once, such as 'string text = File.ReadAllText(\"in.txt\");' or a [GlobalSetup] method."] =
            "C# members of benchmark class, set up once, e.g. 'string text = File.ReadAllText(\"in.txt\");' or [GlobalSetup] method.",
        ["Using directives' namespaces for the cases, such as 'MyApp.Parsing'."] =
            "Using namespaces for cases, e.g. 'MyApp.Parsing'.",
        ["Without cases: which of the project's benchmarks to run, as BenchmarkDotNet's --filter takes it, such as '*Parser*'."] =
            "No cases: project benchmarks to run, BenchmarkDotNet --filter syntax, e.g. '*Parser*'.",
        ["Short (3 iterations, quick and rough) or Default (until the results are stable, minutes per case)."] =
            "Short (3 iterations, rough) or Default (until stable, minutes/case).",

        // aspnet_endpoints
        ["Lists the HTTP endpoints of ASP.NET Core projects from their source: controller actions, with their [Route] and [Http*] templates combined with the controller's, and minimal API Map* calls, with the prefixes of the MapGroup calls they hang off; each with its HTTP method, route, handler and the attributes or calls that add to it, such as [Authorize] or RequireAuthorization. Maps a URL from a log or a bug report to the code that handles it. Routes built at run time, not from constants, show as '?'."] =
            "Lists ASP.NET Core HTTP endpoints from source: controller actions ([Route] + [Http*] combined w/ controller's), minimal API Map* calls (+ MapGroup prefixes); each HTTP method, route, handler, modifiers ([Authorize], RequireAuthorization). URL from log/bug → handler code. Runtime-built routes show '?'.",
        ["Optional regex for the routes to list, e.g. '^/api/orders'."] =
            "Opt. route regex, e.g. '^/api/orders'.",

        // di_registrations
        ["Lists the Microsoft.Extensions.DependencyInjection registrations in source, per service type: each AddSingleton, AddScoped, AddTransient, TryAdd*, AddKeyed* and AddHostedService call with its implementation (a type, or the factory or instance it gives), lifetime and location; which registration wins, the last in source order, as a resolve takes the last; the constructors the service is injected into; and captive dependencies, a scoped or transient service injected into a singleton. Answers which class runs when an interface is injected. Registrations made by scanning assemblies, or through helpers that take the types as parameters, are not seen."] =
            "Lists Microsoft.Extensions.DependencyInjection registrations in source by service type: AddSingleton, AddScoped, AddTransient, TryAdd*, AddKeyed*, AddHostedService w/ implementation (type/factory/instance), lifetime, location; winner (last in source); ctors injected into; captive dependencies (scoped/transient in singleton). Which class an injected interface gets. Misses assembly scanning + helpers taking types as params.",
        ["Optional regex for the service types to list, e.g. 'IOrderStore' or '^MyApp\\.'."] =
            "Opt. service type regex, e.g. 'IOrderStore' or '^MyApp\\.'.",

        // find_duplicates
        ["Finds copied code in the solution's C# files: runs of at least minStatements statements in a row that occur more than once, word for word or with other names and literals (Renamed), each run as long as the copies agree, with the files, lines and members of every copy, the most code first. Before a refactoring, or to find every copy of code a bug was just fixed in. Generated files are left out."] =
            "Finds duplicate/copied C# code: runs of >= minStatements consecutive statements repeated verbatim or w/ other names/literals (Renamed), maximal; files, lines, members per copy, biggest first. Before refactoring, or find copies of fixed bug. Generated files excluded.",
        ["The fewest statements in a row that count as a copy."] =
            "Min consecutive statements per copy.",
        ["How many groups of copies to return."] =
            "Max copy groups.",

        // verify_pending
        ["Lists the snapshots that Verify tests received and that do not match the verified ones: each *.received.* file beside its *.verified.* file, with the diff from the verified file to the received one, or the text of a new snapshot. accept makes received files the verified ones, reject deletes them. Run after run_tests, and accept only a snapshot whose diff is the intended change."] =
            "Lists Verify snapshots received ≠ verified: each *.received.* beside *.verified.* w/ diff, or new snapshot text. accept → becomes verified; reject → deletes. Run after run_tests; accept only intended diffs.",
        ["A .csproj, .sln or .slnx file, or a directory: the received files are looked for below its directory, outside bin and obj."] =
            ".csproj, .sln, .slnx or directory: searched below, excluding bin/obj.",
        ["The received files to accept, as Received names them, or '*' for all."] =
            "Received files to accept (Received names) or '*'.",
        ["The received files to reject, which deletes them, as Received names them, or '*' for all."] =
            "Received files to reject (deletes; Received names) or '*'.",

        // regex_test
        ["Runs a .NET regular expression against sample inputs, as System.Text.RegularExpressions.Regex does, not another engine's dialect: whether each input matches, each match with its position and its groups by number and name, and with replacement what Regex.Replace returns. For writing or checking a pattern before it goes into code, such as into [GeneratedRegex]; no evaluate needed."] =
            "Tests .NET regex (System.Text.RegularExpressions.Regex, not other dialects) on sample inputs: match per input, positions, groups by number/name; replacement → Regex.Replace output. Check pattern before code/[GeneratedRegex]; no evaluate needed.",
        ["The pattern as the string the code passes, after C# unescapes it: \\d+ for @\"\\d+\" or \"\\\\d+\"."] =
            "Pattern as string after C# unescaping: \\d+ for @\"\\d+\" or \"\\\\d+\".",
        ["The inputs to match against."] =
            "Inputs to match.",
        ["RegexOptions, comma-separated, e.g. 'IgnoreCase, Multiline'; also Singleline, ExplicitCapture, IgnorePatternWhitespace, RightToLeft, ECMAScript, CultureInvariant and NonBacktracking."] =
            "RegexOptions comma-separated, e.g. 'IgnoreCase, Multiline'; also Singleline, ExplicitCapture, IgnorePatternWhitespace, RightToLeft, ECMAScript, CultureInvariant, NonBacktracking.",
        ["Optional replacement, as Regex.Replace takes it: $1, ${name}, $0 or $& for the whole match, $$ for a dollar sign."] =
            "Opt. Regex.Replace replacement: $1, ${name}, $0/$& whole match, $$ dollar.",
        ["How many matches to return per input."] =
            "Max matches per input.",

        // resources
        ["Checks the projects' .resx resource files against their translations, Strings.resx against Strings.de.resx and the like: per culture the string resources it lacks, which fall back to the neutral text at run time, and those only it has, which nothing reads. With unused, also the resources no C# code uses, through the generated class's property (Strings.Key) or by name in a string literal, as localizer[\"Key\"] and GetString(\"Key\") do; Razor code counts through the Razor generator's output. Resources used only from XAML or by names built at run time count as unused."] =
            "Checks .resx resources vs translations (Strings.resx vs Strings.de.resx): per culture missing strings (fall back to neutral) + extras (never read). unused → also resources no C# uses via generated property (Strings.Key) or literal name (localizer[\"Key\"], GetString(\"Key\")); Razor counts via generator output. XAML-only or runtime-named uses count unused.",
        ["Optional .resx file, neutral or of a culture: checks only its set."] =
            "Opt. .resx (neutral or culture): check only its set.",
        ["Whether to look for the resources no C# code uses; reads all the code."] =
            "Find resources unused by C#; reads all code.",
    }.ToFrozenDictionary();
}
