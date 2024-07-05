// Copyright (C) 2024 Jefferson Montgomery
// SPDX-License-Identifier: MIT
using System.Xml;

internal class Program
{
    // Helper function to handle Path.GetDirectoryName()==null case (which occurs when path is a
    // root directory e.g. c:\).
    private static string GetDirectoryName(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir == null) {
            dir = path.Substring(0, path.Length - 1);
        }
        return dir;
    }

    private static string GetPathRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return root == null ? string.Empty : root;
    }

    // Helper function to assist XmlNode attribute lookup.
    private static bool TryGetNodeAttribute(XmlNode n, string name,
                                            [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)]
                                            out XmlAttribute? attr)
    {
        attr = n.Attributes == null ? null : n.Attributes[name];
        return attr != null;
    }

    // Functions to replace substrings of the form: ?(...).
    private static string ReplaceHelper1(string s, string start, Func<string, string> getValue)
    {
        for (int i = s.IndexOf(start); i != -1; ) {
            s = ReplaceHelper2(s, start, getValue, i, out var i1, out i);
        }
        return s;
    }

    private static string ReplaceHelper2(string s, string start, Func<string, string> getValue, int i, out int i1, out int i2)
    {
        i1 = s.IndexOf(')', i + 2);
        i2 = s.IndexOf(start, i + 2);

        if (i1 == -1) {
            Console.Error.WriteLine($"error: failed to parse substitution: {s.Substring(i)}");
            Environment.Exit(1);
        }

        if (i2 != -1 && i2 < i1) {
            // TODO
            Console.Error.WriteLine($"error: recursive substitution not yet supported: {s.Substring(i, i2 - i)}");
            Environment.Exit(1);
        }

        var nameLength = i1 - i - 2;
        var name = s.Substring(i + 2, nameLength);
        var value = ReplaceHelper1(getValue(name), start, getValue);

        s = string.Concat(s.AsSpan(0, i), value, s.AsSpan(i1 + 1));

        var delta = value.Length - nameLength - 3;
        i1 += delta;
        if (i2 != -1) {
            i2 += delta;
        }

        return s;
    }

    private static string ReplaceMacros(string s, Dictionary<string, string> macros)
    {
        return ReplaceHelper1(s, "$(", s => macros[s]);
    }

    private static string ReplaceItemMetadata(string s, Item item, string baseProperty)
    {
        return ReplaceHelper1(s, "%(", s => s.Equals(baseProperty, StringComparison.InvariantCultureIgnoreCase)
            ? string.Empty
            : item.GetMetadata(s));
    }

    // Load project name/path pairs out of a solution file.
    private class Project
    {
        public string Name = string.Empty;
        public string Path = string.Empty;
    }

    private static List<Project> LoadSolutionProjects(string slnDir, string slnPath)
    {
        var projects = new List<Project>();
        foreach (var line in File.ReadAllLines(slnPath)) {

            if (line.StartsWith("Project(\"")) {
                int i1 = line.IndexOf('"', 53);
                int i2 = line.IndexOf('"', i1 + 4);

                var name = line.Substring(53, i1 - 53);
                var path = line.Substring(i1 + 4, i2 - i1 - 4);

                path = Path.GetFullPath(Path.Combine(slnDir, path));

                if (!File.Exists(path)) {
                    Console.Error.WriteLine($"error: dependent project does not exist: {path}");
                    Environment.Exit(1);
                }

                projects.Add(new Project{ Name = name, Path = path });
            }
        }
        return projects;
    }

    // Load items out of a project file.
    private class Item
    {
        public string ProjectDir = string.Empty;
        public string Type = string.Empty;
        public string Identity = string.Empty;
        public Dictionary<string, string> Properties = [];

        public string GetMetadata(string name)
        {
            switch (name.ToLower()) {
            case "fullpath":     return Path.Combine(ProjectDir, Identity);
            case "rootdir":      return GetPathRoot(ProjectDir);
            case "filename":     return Path.GetFileNameWithoutExtension(Identity);
            case "extension":    return Path.GetExtension(Identity);
            case "relativedir":  return GetDirectoryName(Identity) + '\\';
            case "directory":    return GetDirectoryName(Path.Combine(ProjectDir, Identity).Substring(GetPathRoot(ProjectDir).Length)) + '\\';
            case "recursivedir": return GetDirectoryName(Path.Combine(ProjectDir, Identity).Substring(GetPathRoot(ProjectDir).Length)) + '\\';
            case "identity":     return Identity;
            case "modifiedtime": return File.GetLastWriteTime(Path.Combine(ProjectDir, Identity)).ToString();
            case "createdtime":  return File.GetCreationTime(Path.Combine(ProjectDir, Identity)).ToString();
            case "accessedtime": return File.GetLastAccessTime(Path.Combine(ProjectDir, Identity)).ToString();
            }
            Console.Error.WriteLine($"error: unknown item metadata: {name}");
            Environment.Exit(1);
            return name;
        }
    }

    private static void AddProject(string prjPath, Dictionary<string, string> globalMacros, List<Item> items, bool verbose)
    {
        // Load the project XML file
        var xml = new XmlDocument();
        xml.Load(prjPath);

        if (xml.DocumentElement == null) {
            Console.Error.WriteLine($"error: failed to load project: {prjPath}");
            Environment.Exit(1);
        }

        // Parse the XML file, adding items
        var baseItemIndex = items.Count;
        var macros = new Dictionary<string, string>(globalMacros, StringComparer.InvariantCultureIgnoreCase);
        var itemDefinitions = new Dictionary<string, Dictionary<string, string>>();

        foreach (XmlNode n in xml.DocumentElement)
        {
            switch (n.Name) {
            case "ItemGroup":
                if (!TryGetNodeAttribute(n, "Label", out var label)) {
                    ParseItemGroup(n, itemDefinitions, items);
                }
                break;

            case "ItemDefinitionGroup":
                if (CheckNodeCondition(n, macros)) {
                    ParseItemDefinitionGroup(n, itemDefinitions);
                }
                break;

            case "PropertyGroup":
                if (CheckNodeCondition(n, macros)) {
                    var props = new Dictionary<string, string>();
                    ParseProperties(n, props);
                    foreach (var kv in props) {
                        macros[kv.Key] = kv.Value;
                    }
                }
                break;
            }
        }

        // Replace any macros and metadata in the added items
        var prjDir = GetDirectoryName(prjPath);

        for (int i = baseItemIndex; i < items.Count; ++i) {
            var item = items[i];
            item.ProjectDir = prjDir;
            item.Identity = ReplaceMacros(item.Identity, macros);

            foreach (var k in item.Properties.Keys) {
                var v = item.Properties[k];
                v = ReplaceItemMetadata(v, item, k.ToLower());
                v = ReplaceMacros(v, macros);
                v = string.Join(';', v.Split(';', StringSplitOptions.RemoveEmptyEntries));
                item.Properties[k] = v;
            }
        }

        // TODO: for include dirs, if !Path.IsPathRooted() incidr=prjdir+incdir

        if (verbose) {
            Console.WriteLine($"{prjPath}");
            foreach (var kv in macros) {
                Console.WriteLine($"    {kv.Key}: {ReplaceMacros(kv.Value, macros)}");
            }

            for (int i = baseItemIndex; i < items.Count; ++i) {
                var item = items[i];

                Console.WriteLine($"    {item.GetMetadata("FullPath")}");
                Console.WriteLine($"        {item.Type}");
                foreach (var kv in item.Properties) {
                    Console.WriteLine($"        {kv.Key}: {kv.Value}");
                }
            }
        }
    }

    // For xml:
    //     <ItemGroup>
    //         <type1 Include="..." />
    //         ...
    //     </ItemGroup>
    // adds type1, etc. to items.
    private static void ParseItemGroup(XmlNode itemGroup, Dictionary<string, Dictionary<string, string>> itemDefinitions, List<Item> items)
    {
        foreach (XmlNode n in itemGroup) {
            if (TryGetNodeAttribute(n, "Include", out var include)) {
                var item = new Item();
                item.Type = n.Name;
                item.Identity = include.Value;

                // Point to the type's properties or create a new properties dictionary if there
                // isn't one, or if we are overwriting properties.
                if (itemDefinitions.ContainsKey(n.Name)) {
                    item.Properties = itemDefinitions[n.Name];

                    if (n.HasChildNodes) {
                        item.Properties = new Dictionary<string, string>(item.Properties);
                    }
                } else {
                    item.Properties = new Dictionary<string, string>();
                }

                ParseProperties(n, item.Properties);

                items.Add(item);
            }
        }
    }

    // For xml:
    //     <ItemDefinitionGroup>
    //         <type1>
    //             <prop1>value1</prop1>
    //             ...
    //         </type1>
    //         ...
    //     </ItemDefinitionGroup>
    // adds type1:Properties1, etc. to itemDefinitions.
    private static void ParseItemDefinitionGroup(XmlNode itemDefinitionGroup, Dictionary<string, Dictionary<string, string>> itemDefinitions)
    {
        foreach (XmlNode n in itemDefinitionGroup) {
            var props = new Dictionary<string, string>();
            ParseProperties(n, props);
            itemDefinitions.Add(n.Name, props);
        }
    }

    // For xml:
    //     <n>
    //       <prop1>value1</prop1>
    //       ...
    //     </n>
    // add prop1:value1, etc. to the props dictionary.
    private static void ParseProperties(XmlNode n, Dictionary<string, string> props)
    {
        foreach (XmlNode p in n) {
            props[p.Name] = p.InnerText;
        }
    }

    // For xml:
    //     <n Condition="...">
    // returns true if there was no condition or the condition was true.
    private static bool CheckNodeCondition(XmlNode n, Dictionary<string, string> macros)
    {
        if (!TryGetNodeAttribute(n, "Condition", out var condAttr)) {
            return true;
        }

        var condition = condAttr.Value;
        var i = condition.IndexOf("==");
        if (i != -1) {
            var l = ReplaceMacros(condition.Substring(0, i), macros);
            var r = ReplaceMacros(condition.Substring(i + 2), macros);
            return l.Equals(r, StringComparison.CurrentCultureIgnoreCase);
        }

        Console.Error.WriteLine($"error: unsupported condition: {condition}");
        Environment.Exit(1);
        return false;
    }

    // Makes a string suitable for the compile_commands.json file.
    private static string ConditionString(string s)
    {
        s = s.Replace('\\', '/');
        return s;
    }

    // Makes a path suitable for the compile_commands.json file.
    private static string ConditionPath(string baseDir, string path)
    {
        if (path.StartsWith(baseDir, StringComparison.CurrentCultureIgnoreCase)) {
            path = path.Substring(baseDir.Length + 1);
        }
        return ConditionString(path);
    }

    // Makes a string suitable for the command portion of the compile_commands.json file.
    private static string AddEscapedQuotesIfNeeded(string s)
    {
        s = ConditionString(s);
        if (s.Any(Char.IsWhiteSpace)) {
            s = $"\\\"{s}\\\"";
        }
        return s;
    }

    // Find a solution or project in the specified directory.
    private static string FindProjectInDirectory(string dir)
    {
        var files = Directory.GetFiles(dir, "*.sln");
        if (files.Length == 1) {
            return files[0];
        }
        if (files.Length > 1) {
            Console.Error.WriteLine($"error: multiple solutions found in directory: {dir}");
            Environment.Exit(1);
        }

        files = Directory.GetFiles(dir, "*.?sproj");
        if (files.Length == 0) {
            Console.Error.WriteLine($"error: no projects found in directory: {dir}");
            Environment.Exit(1);
        }
        if (files.Length > 1) {
            Console.Error.WriteLine($"error: multiple projects found in directory: {dir}");
            Environment.Exit(1);
        }

        return files[0];
    }

    // Parse option value N1=V1;N2=V2...
    private static void ParseProperties(string s, Dictionary<string, string> macros)
    {
        foreach (var p in s.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
            var v = p.Split('=', StringSplitOptions.RemoveEmptyEntries);
            if (v.Length != 2) {
                PrintUsageAndExit($"error: invalid property string: {s}");
            }

            macros[v[0]] = v[1];
        }
    }

    // Print command line usage
    private static void PrintUsageAndExit(string s)
    {
        if (s.Length > 0)  {
            Console.Error.WriteLine(s);
        }

        Console.Error.WriteLine("usage: vsccc [options] [path_to_project]");
        Console.Error.WriteLine("options:");
        Console.Error.WriteLine("    --property:N=V     Provide initial property values.");
        Console.Error.WriteLine("    --verbose          Log extra information while processing.");

        Environment.Exit(1);
    }

    // Console application entrypoint
    private static void Main(string[] args)
    {
        // Initialize default macro values
        //
        // https://learn.microsoft.com/en-us/cpp/build/reference/common-macros-for-build-commands-and-properties?view=msvc-170
        var macros = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase) {
            { "Configuration", "Debug" },
            { "Platform",      "x64" },
            { "IntDir",        "$(SolutionDir)obj\\$(Platform)_$(Configuration)\\$(ProjectName)\\" },
            { "OutDir",        "$(SolutionDir)bin\\$(Platform)_$(Configuration)\\" },
        };

        // Parse command line
        var path = string.Empty;
        var pathSet = 0;
        var verbose = false;

        foreach (var arg in args) {
            if (!arg.StartsWith("-")) {
                path = arg;
                pathSet++;
                continue;
            }

            var a = arg.Substring(1);
            if (a.StartsWith("-")) { a = a.Substring(1); }
            a = a.ToLower();

            if (a == "verbose") { verbose = true; continue; }

            if (a.StartsWith("p:"))        { ParseProperties(a.Substring(2), macros); continue; }
            if (a.StartsWith("property:")) { ParseProperties(a.Substring(9), macros); continue; }

            PrintUsageAndExit($"error: unrecognised option: {arg}");
        }

        if (pathSet > 1) {
            PrintUsageAndExit("error: multiple paths specified");
        }

        // Search for a project file if not explicitly provided
        if (pathSet == 0) {
            path = FindProjectInDirectory(Directory.GetCurrentDirectory());
        } else if (Directory.Exists(path)) {
            path = FindProjectInDirectory(path);
        }

        if (!File.Exists(path)) {
            PrintUsageAndExit($"error: specified project does not exist: {path}");
        }

        // Convert to absolute path
        path = Path.GetFullPath(path);
        var dir = GetDirectoryName(path);

        macros["SolutionDir"] = $"{GetDirectoryName(path)}\\";

        // Load the solution/project items
        var items = new List<Item>();
        if (Path.GetExtension(path) == ".sln") {
            foreach (var prj in LoadSolutionProjects(dir, path)) {
                macros["ProjectName"] = prj.Name;
                AddProject(prj.Path, macros, items, verbose);
            }
        } else {
            macros["ProjectName"] = Path.GetFileNameWithoutExtension(path);
            AddProject(path, macros, items, verbose);
        }

        // Write the compile commands
        var sdir = ConditionString(dir);
        var compiledItemTypes = new Dictionary<string, string> {
            { "ClCompile", "c++" },
        };
        using (var sw = new StreamWriter(Path.Combine(dir, "compile_commands.json"))) {
            sw.Write("[");

            var first = true;
            foreach (var item in items) {
                if (!compiledItemTypes.TryGetValue(item.Type, out var language)) {
                    continue;
                }

                var filePath = ConditionPath(dir, item.GetMetadata("FullPath"));

                if (first) {
                    first = false;
                } else {
                    sw.Write(",");
                }

                sw.WriteLine();
                sw.Write($"{{ \"directory\": \"{sdir}\"");
                sw.Write($", \"file\": \"{filePath}\"");
                sw.Write($", \"command\": \"clang -c -x{language}");

                foreach (var kv in item.Properties) {
                    switch (kv.Key) {
                    case "AdditionalIncludeDirectories":
                        foreach (var inc in kv.Value.Split(';')) {
                            sw.Write($" -I{AddEscapedQuotesIfNeeded(ConditionPath(dir, inc))}");
                        }
                        break;

                    case "PreprocessorDefinitions":
                        foreach (var def in kv.Value.Split(';')) {
                            sw.Write($" -D{def}");
                        }
                        break;

                    case "TreatWarningAsError":
                        if (kv.Value.ToLower() == "true") {
                            sw.Write($" -Werror");
                        }
                        break;
                    }
                }

                sw.Write($" {AddEscapedQuotesIfNeeded(filePath)}\" }}");
            }

            sw.WriteLine();
            sw.WriteLine("]");
        }
    }
}
