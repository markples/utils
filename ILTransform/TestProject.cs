// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace ILTransform
{
    public struct DebugOptimize : IComparable<DebugOptimize>
    {
        public readonly string Debug;
        public readonly string Optimize;

        public DebugOptimize(string debug, string optimize)
        {
            Debug = debug;
            Optimize = optimize;
        }

        public int CompareTo(DebugOptimize other)
        {
            int result = Debug.CompareTo(other.Debug);
            if (result == 0)
            {
                result = Optimize.CompareTo(other.Optimize);
            }
            return result;
        }

        public override bool Equals(object? obj) => obj is DebugOptimize optimize && Debug == optimize.Debug && Optimize == optimize.Optimize;

        public override int GetHashCode() => HashCode.Combine(Debug, Optimize);

        public override string ToString()
        {
            return string.Format("DbgOpt ({0} | {1})", Debug, Optimize);
        }
    }

    public struct TestCount
    {
        public int Total;
        public int Pri0;
        public int Fact;
        public int ILProj;
        public HashSet<string> Properties;
        public HashSet<string> ItemGroups;

        public int Pri1 => Total - Pri0;

        public static TestCount New() => new TestCount() { Properties = new HashSet<string>(), ItemGroups = new HashSet<string>() };
    }

    public struct SourceInfo
    {
        public SourceInfo() { }

        public List<string> TypeNames = new List<string>();
        public string MainClassName = "";
        public List<string> MainClassBases = new List<string>();
        public string MainClassNamespace = "";
        public string MainClassSourceFile = "";
        public int MainClassLine = -1;
        public string MainMethodName = "";
        public int FirstMainMethodDefLine = -1;
        public int MainTokenMethodLine = -1;
        public int LastMainMethodDefLine = -1;
        public int LastMainMethodBodyLine = -1;
        public int LastMainMethodBodyColumn = -1;
        public int LastHeaderCommentLine = -1; // will include one blank after comments if it exists
        public int LastUsingLine = -1;
        public int NamespaceLine = -1;
        public int NamespaceIdentLine = -1;
        public bool HasFactAttribute = false;
        public bool HasExit = false;
    }

    public struct TestProjectPathEqualityComparer : IEqualityComparer<(string, TestProject)>
    {
        public bool Equals((string, TestProject) project1, (string, TestProject) project2)
        {
            if (project1.Item2 == null || project2.Item2 == null) return object.ReferenceEquals(project1.Item2, project2.Item2);
            return project1.Item2.AbsolutePath == project2.Item2.AbsolutePath;
        }

        public int GetHashCode((string, TestProject) project) => project.Item2.GetHashCode();
    }

    public class TestProject
    {
        public readonly string AbsolutePath;
        public readonly string RelativePath;
        public readonly string OutputType;
        public readonly string CLRTestKind;
        public readonly string Priority;
        public readonly string CLRTestProjectToRun;
        public readonly string CLRTestExecutionArguments;
        public readonly DebugOptimize DebugOptimize;
        public readonly bool HasRequiresProcessIsolation;
        public readonly string[] RequiresProcessIsolationReasons;
        public readonly string[] CompileFiles;
        public readonly bool CompileFilesIncludeProjectName;
        public readonly string[] ProjectReferences;
        public readonly SourceInfo SourceInfo;
        public List<string> TypeNames => SourceInfo.TypeNames;
        public string MainClassName => SourceInfo.MainClassName;
        public List<string> MainClassBases => SourceInfo.MainClassBases;
        public string MainClassNamespace => SourceInfo.MainClassNamespace;
        public string MainClassSourceFile => NewTestClassSourceFile ?? SourceInfo.MainClassSourceFile;
        public int MainClassLine => SourceInfo.MainClassLine;
        public string MainMethodName => SourceInfo.MainMethodName;
        public int FirstMainMethodDefLine => SourceInfo.FirstMainMethodDefLine;
        public int MainTokenMethodLine => SourceInfo.MainTokenMethodLine;
        public int LastMainMethodDefLine => SourceInfo.LastMainMethodDefLine;
        public int LastMainMethodBodyLine => SourceInfo.LastMainMethodBodyLine;
        public int LastMainMethodBodyColumn => SourceInfo.LastMainMethodBodyColumn;
        public int LastHeaderCommentLine => SourceInfo.LastHeaderCommentLine;
        public int LastUsingLine => SourceInfo.LastUsingLine;
        public int NamespaceLine => SourceInfo.NamespaceLine;
        public int NamespaceIdentLine => SourceInfo.NamespaceIdentLine;
        public bool HasFactAttribute => SourceInfo.HasFactAttribute;
        public Dictionary<string, string> AllProperties;
        public HashSet<string> AllItemGroups;

        public readonly bool IsILProject;

        public string? TestProjectAlias;
        public string? DeduplicatedNamespaceName;
        public bool AddedFactAttribute = false;
        public string? NewAbsolutePath;
        public string? NewTestClassSourceFile;

        public TestProject(
            string absolutePath,
            string relativePath,
            Dictionary<string, string> allProperties,
            HashSet<string> allItemGroups,
            string[] compileFiles,
            bool compileFilesIncludeProjectName,
            string[] projectReferences,
            SourceInfo sourceInfo)
        {
            AbsolutePath = absolutePath;
            RelativePath = relativePath;
            AllProperties = allProperties;
            AllItemGroups = allItemGroups;

            OutputType = GetProperty("OutputType");
            CLRTestKind = GetProperty("CLRTestKind");
            Priority = GetProperty("CLRTestPriority");
            CLRTestProjectToRun = SanitizeFileName(GetProperty("CLRTestProjectToRun"), AbsolutePath);
            CLRTestExecutionArguments = GetProperty("CLRTestExecutionArguments");
            string debugType = InitCaps(GetProperty("DebugType"));
            string optimize = InitCaps(GetProperty("Optimize"));
            if (optimize == "")
            {
                optimize = "False";
            }
            DebugOptimize = new DebugOptimize(debugType, optimize);
            string? requiresProcessIsolation = GetProperty("RequiresProcessIsolation", null);
            if (requiresProcessIsolation == "true")
            {
                HasRequiresProcessIsolation = true;
            }
            else if (requiresProcessIsolation != null)
            {
                Console.WriteLine("New value {0} for RequiresProcessIsolation in {1}", requiresProcessIsolation, AbsolutePath);
            }
            RequiresProcessIsolationReasons =
                TestProjectStore.RequiresProcessIsolationProperties.Where(HasProperty).Concat(
                    TestProjectStore.RequiresProcessIsolationItemGroups.Where(HasItemGroup)).Concat(
                        new[] { "Environment.Exit" }.Where(_ => sourceInfo.HasExit)).ToArray();

            CompileFiles = compileFiles;
            CompileFilesIncludeProjectName = compileFilesIncludeProjectName;
            ProjectReferences = projectReferences;
            SourceInfo = sourceInfo;

            IsILProject = Path.GetExtension(RelativePath).ToLower() == ".ilproj";

            if (IsILProject && CompileFiles.Length != 1)
            {
                Console.WriteLine($"More than one IL file in {AbsolutePath}");
            }
        }

        public bool NeedsRequiresProcessIsolation => RequiresProcessIsolationReasons.Length > 0;

        public static bool IsIdentifier(char c)
        {
            return char.IsDigit(c) || char.IsLetter(c) || c == '_' || c == '@' || c == '$' || c == '`';
        }

        private static Regex PublicRegex = new Regex(@"(\s|^)public(\s|$)");
        private static Regex NotPublicRegex = new Regex(@"(?:private|internal)(?<ws>\s+)");

        // Side effect: Changes private/internal to public in 'line'
        // Side effect (force==true): If above fails, adds "public" to 'line'
        public static bool MakePublic(bool isILTest, ref string line, bool force)
        {
            if (PublicRegex.IsMatch(line))
            {
                return false;
            }

            string replacedLine = NotPublicRegex.Replace(line, match => "public" + match.Groups["ws"]);
            if (!object.ReferenceEquals(line, replacedLine))
            {
                line = replacedLine;
                return true;
            }

            if (force)
            {
                int charIndex = line.SkipWhiteSpace();
                string preffix = line[..charIndex];
                if (isILTest)
                {
                    // They always start like .method or .class but might end without spaces
                    charIndex = line.SkipNonWhiteSpace(charIndex);
                    preffix = string.Concat(line.AsSpan(0, charIndex), " "); // Add space and move index
                    if (charIndex < line.Length)
                    {
                        charIndex++;
                    }
                }
                // Avoid white space at the end if line is ".class" or add white space if something continues
                // after the new "public" token.
                string suffix = charIndex == line.Length ? "" : string.Concat(" ", line.AsSpan(charIndex));
                line = string.Concat(preffix, "public", suffix);
                return true;
            }

            return false;
        }

        public static string SanitizeIdentifier(string source)
        {
            StringBuilder output = new StringBuilder();
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                if (IsIdentifier(c))
                {
                    if (char.IsDigit(c) && output.Length == 0)
                    {
                        output.Append('_');
                    }
                    output.Append(c);
                }
                else if (c == '-')
                {
                    output.Append('_');
                }
                else
                {
                    output.Append("__");
                }
            }
            return output.ToString();
        }

        public static int GetIndent(string line)
        {
            int indentIndex = 0;
            while (indentIndex < line.Length && line[indentIndex] <= ' ')
            {
                indentIndex++;
            }
            return indentIndex;
        }

        public static string AddAfterIndent(string line, string add)
        {
            int indentIndex = GetIndent(line);
            return string.Concat(line.AsSpan(0, indentIndex), add, line.AsSpan(indentIndex));
        }

        public static string ReplaceIdentifier(string line, string originalIdent, string targetIdent)
        {
            int startIndex = 0;
            while (startIndex < line.Length)
            {
                int index = line.IndexOf(originalIdent, startIndex);
                if (index < 0)
                {
                    break;
                }
                int endIndex = index + originalIdent.Length;
                if ((index == 0 || !IsIdentifier(line[index - 1]))
                    && (endIndex >= line.Length || !IsIdentifier(line[endIndex])))
                {
                    line = string.Concat(line.AsSpan(0, index), targetIdent, line.AsSpan(endIndex));
                    startIndex = index + targetIdent.Length;
                }
                else
                {
                    startIndex = index + 1;
                }
            }
            return line;
        }

        public static bool TryGetILNamespaceName(string path, string line, out string namespaceName)
        {
            string[] components = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (components.Length < 2 || components[0] != ".namespace")
            {
                namespaceName = "";
                return false;
            }

            namespaceName = components[1];
            if (namespaceName.StartsWith("\'"))
            {
                namespaceName = namespaceName.Substring(1, namespaceName.Length - 2);
            }
            return true;
        }

        public static bool TryGetILTypeName(string path, List<string> lines, int lineIndex, out string typeName)
        {
            string line = lines[lineIndex];
            if (!line.IndicesOf(".class", out _, out int scanIndex)
                && !line.IndicesOf(".struct", out _, out scanIndex)
                && !line.IndicesOf(".interface", out _, out scanIndex))
            {
                typeName = "";
                return false;
            }

            while (lineIndex < lines.Count || scanIndex < line.Length)
            {
                scanIndex = line.SkipWhiteSpace(scanIndex);
                if (lineIndex + 1 < lines.Count && scanIndex == line.Length)
                {
                    lineIndex++;
                    line = lines[lineIndex];
                    scanIndex = 0;
                    continue;
                }
                if (scanIndex + 1 < line.Length && line[scanIndex] == '/' && line[scanIndex + 1] == '*')
                {
                    scanIndex += 2;
                    while (scanIndex + 1 < line.Length && !(line[scanIndex] == '*' && line[scanIndex + 1] == '/'))
                    {
                        scanIndex++;
                    }
                    scanIndex += 2;
                    continue;
                }
                if (line[scanIndex] == '\'')
                {
                    int identStart = ++scanIndex;
                    while (scanIndex < line.Length && line[scanIndex] != '\'')
                    {
                        scanIndex++;
                    }
                    typeName = line.Substring(identStart, scanIndex - identStart);
                    return true;
                }
                if (IsIdentifier(line[scanIndex]))
                {
                    int identStart = scanIndex;
                    while (++scanIndex < line.Length && (IsIdentifier(line[scanIndex]) || (line[scanIndex] == '.')))
                    {
                    }
                    typeName = line.Substring(identStart, scanIndex - identStart);
                    switch (typeName)
                    {
                        case "auto":
                        case "ansi":
                        case "interface":
                        case "public":
                        case "private":
                        case "sealed":
                        case "value":
                        case "beforefieldinit":
                        case "sequential":
                        case "explicit":
                        case "abstract":
                            continue;

                        case "nested":
                            // This is a bit precarious, but we're going to just ignore nested types for now.
                            // For example, if an entrypoint in NS1.NS2.C1/C2::Main, we'll skip the .class for
                            // C2 and end up at the C1.  It's probably ok to just be checking (and possibly)
                            // renaming C1 there, but it will be something to watch.
                            typeName = "";
                            return false;

                        default:
                            return true;
                    }
                }
                break; // parse error
            }
            Console.WriteLine("Found IL class/struct with no type name in {0}", path);
            typeName = "";
            return false;
        }

        public bool HasSameContentAs(TestProject project2)
        {
            if (CompileFiles.Length == 0 || project2.CompileFiles.Length == 0)
            {
                return false;
            }
            if (ProjectReferences.Length != project2.ProjectReferences.Length)
            {
                return false;
            }
            if (CompileFiles.Length != project2.CompileFiles.Length)
            {
                return false;
            }
            for (int refIndex = 0; refIndex < ProjectReferences.Length; refIndex++)
            {
                string ref1 = ProjectReferences[refIndex];
                string ref2 = project2.ProjectReferences[refIndex];
                try
                {
                    if (ref1 != ref2 && File.ReadAllText(ref1) != File.ReadAllText(ref2))
                    {
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error comparing projects {ref1} and {ref2} referenced from {AbsolutePath} and {project2.AbsolutePath}: {ex.Message}");
                    return false;
                }
            }
            for (int fileIndex = 0; fileIndex < CompileFiles.Length; fileIndex++)
            {
                string file1 = CompileFiles[fileIndex];
                string file2 = project2.CompileFiles[fileIndex];
                try
                {
                    if (file1 != file2 && File.ReadAllText(file1) != File.ReadAllText(file2))
                    {
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error comparing files {file1} and {file2} referenced from {AbsolutePath} and {project2.AbsolutePath}: {ex.Message}");
                    return false;
                }
            }
            if (AllProperties.GetValueOrDefault("DefineConstants") != project2.AllProperties.GetValueOrDefault("DefineConstants"))
            {
                return false;
            }
            // Should we look at all Properties and ItemGroups?
            return true;
        }

        public static void GetKeyNameRootNameAndSuffix(string path, out string keyName, out string rootName, out string suffix)
        {
            string fileName = Path.GetFileName(path);
            suffix = Path.GetExtension(fileName);
            rootName = Path.GetFileNameWithoutExtension(fileName);
            int suffixIndex = rootName.Length;
            int keyNameIndex = suffixIndex;
            if (rootName.EndsWith("_il_ro") || rootName.EndsWith("_il_do"))
            {
                suffixIndex -= 6;
                keyNameIndex = suffixIndex;
            }
            else if (rootName.EndsWith("_cs_ro") || rootName.EndsWith("_cs_do"))
            {
                suffixIndex -= 6;
            }
            else if (rootName.EndsWith("_il_r") || rootName.EndsWith("_il_d"))
            {
                suffixIndex -= 5;
                keyNameIndex = suffixIndex;
            }
            else if (rootName.EndsWith("_cs_r") || rootName.EndsWith("_cs_d"))
            {
                suffixIndex -= 5;
            }
            else if (rootName.EndsWith("_do") || rootName.EndsWith("_ro"))
            {
                suffixIndex -= 3;
            }
            else if (rootName.EndsWith("_d") || rootName.EndsWith("_r"))
            {
                suffixIndex -= 2;
            }
            keyName = rootName.Substring(0, keyNameIndex);
            suffix = string.Concat(rootName.AsSpan(suffixIndex), suffix);
            rootName = rootName.Substring(0, suffixIndex);
        }

        public void GetKeyNameRootNameAndSuffix(out string keyName, out string rootName, out string suffix)
            => GetKeyNameRootNameAndSuffix(RelativePath, out keyName, out rootName, out suffix);

        [return: NotNullIfNotNull("defaultValue")]
        private string? GetProperty(string name, string? defaultValue = "")
            => AllProperties.TryGetValue(name, out string? property) ? property : defaultValue;

        private bool HasProperty(string name) => AllProperties.ContainsKey(name);
        private bool HasItemGroup(string name) => AllItemGroups.Contains(name);

        private static string SanitizeFileName(string fileName, string projectPath)
        {
            string projectName = Path.GetFileNameWithoutExtension(projectPath);
            return Path.GetFullPath(
                fileName
                .Replace("$(MSBuildProjectName)", projectName)
                .Replace("$(MSBuildThisFileName)", projectName),
                Path.GetDirectoryName(projectPath)!);
        }

        private static string InitCaps(string s)
        {
            if (s.Length > 0)
            {
                s = string.Concat(s.Substring(0, 1).ToUpper(), s.AsSpan(1));
            }
            if (s.Equals("pdbonly", StringComparison.OrdinalIgnoreCase))
            {
                s = "PdbOnly";
            }
            return s;
        }
    }

    public class TestProjectStore
    {
        private readonly List<TestProject> _projects;
        private readonly Dictionary<string, List<TestProject>> _classNameMap;
        private readonly Dictionary<string, Dictionary<DebugOptimize, List<TestProject>>> _classNameDbgOptMap;
        private readonly HashSet<string> _rewrittenFiles;
        private readonly Dictionary<string, string> _movedFiles;

        public TestProjectStore()
        {
            _projects = new List<TestProject>();
            _classNameMap = new Dictionary<string, List<TestProject>>();
            _classNameDbgOptMap = new Dictionary<string, Dictionary<DebugOptimize, List<TestProject>>>();
            _rewrittenFiles = new HashSet<string>();
            _movedFiles = new Dictionary<string, string>();
        }

        public void ScanTrees(IReadOnlyList<string> rootPaths)
        {
            int projectCount = 0;
            Stopwatch sw = Stopwatch.StartNew();
            foreach (string rootPath in rootPaths)
            {
                ScanRecursive(rootPath, "", ref projectCount);
            }
            PopulateClassNameMap();
            Console.WriteLine("Done scanning {0} projects in {1} msecs", projectCount, sw.ElapsedMilliseconds);
        }

        // Side effect: Sets TestProjectAlias
        public void GenerateExternAliases()
        {
            foreach (TestProject project in _projects)
            {
                project.TestProjectAlias = Path.GetFileNameWithoutExtension(project.RelativePath);
            }
        }

        private static string[] s_standardProperties = new string[]
        {
            "OutputType",
            "CLRTestKind",
            "CLRTestPriority",
            "DebugType",
            "Optimize",

            "RequiresProcessIsolation",

            // Build-time options aren't relevant to merging
            "AllowUnsafeBlocks",
            "Noconfig",
            "NoStandardLib",
            "DefineConstants",
            "NoWarn",
            "DisableProjectBuild", // no runtime considerations - it's either there or not
            "RestorePackages",
            "TargetFramework", //! VERIFY
            "ReferenceXUnitWrapperGenerator", //! VERIFY -- according a test comment, this should block Main->TestEntryPoint, etc.
            "EnableUnsafeBinaryFormatterSerialization", //! VERIFY
        };

        public static string[] RequiresProcessIsolationProperties = new string[]
        {
            "CLRTestTargetUnsupported",
            "GCStressIncompatible",
            "UnloadabilityIncompatible",
            "JitOptimizationSensitive",
            "TieringTestIncompatible",
            "HeapVerifyIncompatible",
            "IlasmRoundTripIncompatible",
            "SynthesizedPgoIncompatible",
            "CrossGenTest",
        };

        public static string[] s_standardItemGroups = new string[]
        {
            "Compile",
            "ProjectReference",
        };

        public static string[] RequiresProcessIsolationItemGroups = new string[]
        {
            "CLRTestBashEnvironmentVariable",
            "CLRTestBatchEnvironmentVariable",
            "CLRTestEnvironmentVariable",
            "Content",
            "CMakeProjectReference",
        };

        public void DumpFolderStatistics(TextWriter writer)
        {
            for (int level = 1; level <= 5; level++)
            {
                string title = string.Format("COUNT |  PRI0  |  PRI1  |  FACT  | ILPROJ | TO FIX | {0} (PROPERTIES)", level);
                writer.WriteLine(title);
                writer.WriteLine(new string('-', title.Length));
                Dictionary<string, TestCount> folderCounts = new Dictionary<string, TestCount>();

                foreach (TestProject project in _projects.Where(p => p.MainClassName != ""))
                {
                    string[] folderSplit = project.RelativePath.Split(Path.DirectorySeparatorChar);
                    StringBuilder folderNameBuilder = new StringBuilder();
                    for (int component = 0; component < folderSplit.Length - 1 && component < level; component++)
                    {
                        if (folderNameBuilder.Length != 0)
                        {
                            folderNameBuilder.Append('/');
                        }
                        folderNameBuilder.Append(folderSplit[component]);
                    }
                    string folderName = folderNameBuilder.ToString();
                    if (!folderCounts.TryGetValue(folderName, out TestCount count))
                    {
                        count = TestCount.New();
                    }
                    count.Total++;
                    if (project.Priority != "1")
                    {
                        count.Pri0++;
                    }
                    if (project.HasFactAttribute)
                    {
                        count.Fact++;
                    }
                    if (Path.GetExtension(project.RelativePath).ToLower() == ".ilproj")
                    {
                        count.ILProj++;
                    }
                    count.Properties!.UnionWith(project.AllProperties.Keys);
                    count.ItemGroups!.UnionWith(project.AllItemGroups);
                    folderCounts[folderName] = count;
                }
                foreach (KeyValuePair<string, TestCount> kvp in folderCounts.OrderBy(kvp => kvp.Key))
                {
                    string props = string.Join(
                        ' ',
                        kvp.Value.Properties
                            .Except(s_standardProperties)
                            .Except(RequiresProcessIsolationProperties)
                            .OrderBy(prop => prop));
                    string itemGroups = string.Join(
                        ' ',
                        kvp.Value.ItemGroups
                            .Except(s_standardItemGroups)
                            .Except(RequiresProcessIsolationItemGroups)
                            .OrderBy(prop => prop));

                    writer.WriteLine(
                        "{0,5} | {1,6} | {2,6} | {3,6} | {4,6} | {5,6} | {6} ({7})",
                        kvp.Value.Total,
                        kvp.Value.Pri0,
                        kvp.Value.Pri1,
                        kvp.Value.Fact,
                        kvp.Value.ILProj,
                        kvp.Value.Total - kvp.Value.Fact,
                        kvp.Key,
                        props);
                    if (!string.IsNullOrEmpty(itemGroups))
                    {
                        writer.WriteLine(
                            "{0} | ({1})",
                            new string(' ', 5 + 3 + 6 + 3 + 6 + 3 + 6 + 3 + 6 + 3 + 6),
                            itemGroups);
                    }
                }
                writer.WriteLine();
            }
        }

        public void DumpDebugOptimizeStatistics(TextWriter writer)
        {
            Dictionary<DebugOptimize, int> debugOptimizeCountMap = new Dictionary<DebugOptimize, int>();

            foreach (TestProject project in _projects)
            {
                debugOptimizeCountMap.TryGetValue(project.DebugOptimize, out int projectCount);
                debugOptimizeCountMap[project.DebugOptimize] = projectCount + 1;
            }

            writer.WriteLine("DEBUG      | OPTIMIZE   | PROJECT COUNT");
            writer.WriteLine("----------------------------------------");

            foreach (KeyValuePair<DebugOptimize, int> kvp in debugOptimizeCountMap.OrderByDescending(kvp => kvp.Value))
            {
                writer.WriteLine("{0,-10} | {1,-10} | {2}", kvp.Key.Debug, kvp.Key.Optimize, kvp.Value);
            }
            writer.WriteLine();
        }

        public void DumpIrregularProjectSuffixes(TextWriter writer)
        {
            var configs = new (string, string, Func<string, bool>)[] {
                ("ilproj",
                "ending in _d/do/r/ro without _il",
                p => (p.EndsWith("_il_do") || p.EndsWith("_il_ro") || p.EndsWith("_il_d") || p.EndsWith("_il_r"))
                    && !p.EndsWith("_il_do") && !p.EndsWith("_il_ro") && !p.EndsWith("_il_d") && !p.EndsWith("_il_r")),

                //("ilproj",
                //"not ending in _il_d/do/r/ro",
                //p => !p.EndsWith("_il_do") && !p.EndsWith("_il_ro") && !p.EndsWith("_il_d") && !p.EndsWith("_il_r")),

                ("csproj",
                "ending in _il_d/do/r/ro",
                p => p.EndsWith("_il_do") || p.EndsWith("_il_ro") || p.EndsWith("_il_d") || p.EndsWith("_il_r")),

                //("csproj",
                //"not ending in _d/_do/_r/_ro",
                //p => !p.EndsWith("_do") && !p.EndsWith("_ro") && !p.EndsWith("_d") && !p.EndsWith("_r")),
            };

            foreach ((string ext, string desc, var pred) in configs)
            {
                string extCheck = "." + ext;

                var displayProject = (TestProject project) =>
                    (Path.GetExtension(project.RelativePath).ToLower() == extCheck)
                    && pred(Path.GetFileNameWithoutExtension(project.RelativePath));

                if (!_projects.Any(displayProject))
                {
                    continue;
                }

                string header = $"{ext.ToUpper()} projects {desc}";
                writer.WriteLine(header);
                writer.WriteLine(new string('-', header.Length));

                _projects.Where(displayProject).ToList().ForEach(project => writer.WriteLine(project.AbsolutePath));
                writer.WriteLine();
            }
        }

        public void DumpMultiSourceProjects(TextWriter writer)
        {
            writer.WriteLine("PROJECT WITH MULTIPLE SOURCES");
            writer.WriteLine("-----------------------------");
            _projects.Where(p => p.CompileFiles.Length > 1).Select(p => p.AbsolutePath).ToList().ForEach(writer.WriteLine);
            writer.WriteLine();
        }

        public void DumpMultiProjectSources(TextWriter writer)
        {
            Dictionary<string, Dictionary<DebugOptimize, List<TestProject>>> potentialDuplicateMap = new Dictionary<string, Dictionary<DebugOptimize, List<TestProject>>>();
            foreach (TestProject project in _projects)
            {
                foreach (string source in project.CompileFiles)
                {
                    Utils.AddToNestedMultiMap(potentialDuplicateMap, source, project.DebugOptimize, project);
                }
            }

            writer.WriteLine("SOURCES USED IN MULTIPLE PROJECTS");
            writer.WriteLine("---------------------------------");

            foreach (KeyValuePair<string, Dictionary<DebugOptimize, List<TestProject>>> sourceKvp in potentialDuplicateMap.Where(kvp => kvp.Value.Values.Any(l => l.Count > 1)).OrderBy(kvp => kvp.Key))
            {
                writer.WriteLine(sourceKvp.Key);
                foreach (KeyValuePair<DebugOptimize, List<TestProject>> debugOptKvp in sourceKvp.Value.Where(kvp => kvp.Value.Count > 1))
                {
                    writer.WriteLine("\\- {0}", debugOptKvp.Key);
                    foreach (TestProject project in debugOptKvp.Value)
                    {
                        writer.WriteLine("   \\- {0}", project.AbsolutePath);
                    }
                }
            }

            writer.WriteLine();
        }

        public void DumpDuplicateProjectContent(TextWriter writer)
        {
            Dictionary<string, List<TestProject>> potentialDuplicateMap = new Dictionary<string, List<TestProject>>();
            foreach (TestProject project in _projects)
            {
                StringBuilder projectKey = new StringBuilder();
                projectKey.AppendLine("Debug: " + project.DebugOptimize.Debug.ToLower());
                projectKey.AppendLine("Optimize: " + project.DebugOptimize.Optimize.ToLower());
                foreach (string projectReference in project.ProjectReferences.Select(p => Path.GetFileName(p)).OrderBy(p => p))
                {
                    projectKey.AppendLine("ProjectReference: " + projectReference);
                }
                foreach (string compileFile in project.CompileFiles.Select(p => Path.GetFileName(p)).OrderBy(p => p))
                {
                    projectKey.AppendLine("CompileFile: " + compileFile);
                }
                string key = projectKey.ToString();
                Utils.AddToMultiMap(potentialDuplicateMap, key, project);
            }

            writer.WriteLine("PROJECT PAIRS WITH DUPLICATE CONTENT");
            writer.WriteLine("------------------------------------");
            bool first = true;
            foreach (List<TestProject> projectGroup in potentialDuplicateMap.Values)
            {
                for (int index1 = 1; index1 < projectGroup.Count; index1++)
                {
                    for (int index2 = 0; index2 < index1; index2++)
                    {
                        TestProject project1 = projectGroup[index1];
                        TestProject project2 = projectGroup[index2];
                        if (project1.HasSameContentAs(project2))
                        {
                            if (first)
                            {
                                writer.WriteLine();
                                first = false;
                            }
                            writer.WriteLine(project1.AbsolutePath);
                            writer.WriteLine(project2.AbsolutePath);
                        }
                    }
                }
            }
            writer.WriteLine();
        }

        public void DumpDuplicateSimpleProjectNames(TextWriter writer)
        {
            Dictionary<string, List<TestProject>> simpleNameMap = new Dictionary<string, List<TestProject>>();
            foreach (TestProject project in _projects)
            {
                string simpleName = Path.GetFileNameWithoutExtension(project.RelativePath);
                Utils.AddToMultiMap(simpleNameMap, simpleName, project);
            }

            foreach (KeyValuePair<string, List<TestProject>> kvp in simpleNameMap.Where(kvp => kvp.Value.Count > 1).OrderByDescending(kvp => kvp.Value.Count))
            {
                writer.WriteLine("DUPLICATE PROJECT NAME: ({0}x): {1}", kvp.Value.Count, kvp.Key);
                foreach (TestProject project in kvp.Value)
                {
                    writer.WriteLine("    {0}", project.AbsolutePath);
                }
                writer.WriteLine();
            }
        }

        public void DumpDuplicateEntrypointClasses(TextWriter writer)
        {
            Dictionary<string, List<TestProject>> duplicateClassNames = new Dictionary<string, List<TestProject>>();
            foreach (KeyValuePair<string, List<TestProject>> kvp in _classNameMap.Where(kvp => kvp.Value.Count > 1))
            {
                Dictionary<DebugOptimize, List<TestProject>> debugOptMap = new Dictionary<DebugOptimize, List<TestProject>>();
                foreach (TestProject project in kvp.Value)
                {
                    Utils.AddToMultiMap(debugOptMap, project.DebugOptimize, project);
                }
                List<TestProject> filteredDuplicates = new List<TestProject>();
                foreach (List<TestProject> projectList in debugOptMap.Values.Where(v => v.Count > 1))
                {
                    filteredDuplicates.AddRange(projectList);
                }
                if (filteredDuplicates.Count > 0)
                {
                    duplicateClassNames.Add(kvp.Key, filteredDuplicates);
                }
            }

            writer.WriteLine("#PROJECTS | DUPLICATE TEST CLASS NAME");
            writer.WriteLine("-------------------------------------");

            foreach (KeyValuePair<string, List<TestProject>> kvp in duplicateClassNames.Where(kvp => kvp.Value.Count > 1).OrderByDescending(kvp => kvp.Value.Count))
            {
                writer.WriteLine("{0,-9} | {1}", kvp.Value.Count, kvp.Key);
            }

            writer.WriteLine();

            foreach (KeyValuePair<string, List<TestProject>> kvp in _classNameMap.Where(kvp => kvp.Value.Count > 1).OrderByDescending(kvp => kvp.Value.Count))
            {
                string title = string.Format("{0} PROJECTS WITH CLASS NAME {1}:", kvp.Value.Count, kvp.Key);
                writer.WriteLine(title);
                writer.WriteLine(new string('-', title.Length));
                foreach (TestProject project in kvp.Value.OrderBy(prj => prj.RelativePath))
                {
                    writer.WriteLine("{0} -> {1}", project.AbsolutePath, project.DeduplicatedNamespaceName);
                }
                writer.WriteLine();
            }

            writer.WriteLine();
        }

        public void DumpImplicitSharedLibraries(TextWriter writer)
        {
            writer.WriteLine("IMPLICIT SHARED LIBRARIES");
            writer.WriteLine("-------------------------");

            foreach (TestProject project in _projects.Where(p => p.OutputType.Equals("Library", StringComparison.OrdinalIgnoreCase) && p.CLRTestKind == "").OrderBy(p => p.RelativePath))
            {
                writer.WriteLine(project.AbsolutePath);
            }

            writer.WriteLine();
        }

        public void DumpProjectsWithoutFactAttributes(TextWriter writer)
        {
            writer.WriteLine("PROJECTS WITH ADDED FACT ATTRIBUTES: {0}", _projects.Where(p => p.AddedFactAttribute).Count());
            writer.WriteLine();

            writer.WriteLine("PROJECTS REMAINING WITHOUT FACT ATTRIBUTES (Count={0})",
                _projects.Where(p => !p.HasFactAttribute && !p.AddedFactAttribute).Count());
            writer.WriteLine("------------------------------------------");

            _projects.Where(p => p.MainClassName != "" && !p.HasFactAttribute && !p.AddedFactAttribute)
                .OrderBy(p => p.RelativePath)
                .Select(p => p.AbsolutePath)
                .ToList()
                .ForEach(writer.WriteLine);

            writer.WriteLine();
        }

        public void DumpCommandLineVariations(TextWriter writer)
        {
            Dictionary<string, List<TestProject>> commandlineVariations = new Dictionary<string, List<TestProject>>();
            foreach (TestProject project in _projects.Where(p => p.CLRTestExecutionArguments != ""))
            {
                Utils.AddToMultiMap(commandlineVariations, project.CLRTestProjectToRun, project);
            }

            if (commandlineVariations.TryGetValue("", out List<TestProject>? singleProjects))
            {
                writer.WriteLine("SINGLE TESTS WITH COMMAND-LINE ARGUMENTS");
                writer.WriteLine("----------------------------------------");
                foreach (TestProject project in singleProjects.OrderBy(p => p.RelativePath))
                {
                    writer.WriteLine("{0} -> {1}", project.AbsolutePath, project.CLRTestExecutionArguments);
                }
            }
            writer.WriteLine();

            writer.WriteLine("TEST GROUPS WITH VARIANT ARGUMENTS");
            writer.WriteLine("----------------------------------");
            foreach (KeyValuePair<string, List<TestProject>> group in commandlineVariations.OrderByDescending(clv => clv.Value.Count))
            {
                writer.WriteLine(group.Key);
                foreach (TestProject project in group.Value.OrderBy(p => p.RelativePath))
                {
                    writer.WriteLine("    -> {0}", project.CLRTestExecutionArguments);
                }
            }
            writer.WriteLine();
        }

        // Side effects: Rewrite CS/IL source files, rewrite proj files
        public void RewriteAllTests(Settings settings)
        {
            Stopwatch sw = Stopwatch.StartNew();

            int index = 0;
            foreach (TestProject project in _projects)
            {
                if (!string.IsNullOrEmpty(settings.ClassToDeduplicate) && project.MainClassName != settings.ClassToDeduplicate)
                {
                    continue;
                }
                new ILRewriter(
                    project,
                    settings,
                    _rewrittenFiles).Rewrite();
                index++;
                if (index % 500 == 0)
                {
                    Console.WriteLine("Rewritten {0} / {1} projects", index, _projects.Count);
                }
            }
            Console.WriteLine("Done rewriting {0} projects in {1} msecs", _projects.Count, sw.ElapsedMilliseconds);
        }

        private static string RenameFileBase(string original, string endToReplace, string newEnd)
        {
            if (original.EndsWith(newEnd)) return original;
            if (!original.EndsWith(endToReplace)) return original;
            return string.Concat(original.AsSpan(0, original.Length - endToReplace.Length), newEnd);
        }

        // Side effect: renames project files
        public void UnifyDbgRelProjects()
        {
            foreach (TestProject testProject in _projects)
            {
                string dir = Path.GetDirectoryName(testProject.AbsolutePath)!;
                string file = Path.GetFileNameWithoutExtension(testProject.RelativePath);
                string ext = Path.GetExtension(testProject.RelativePath);
                string renamedFile = file;
                // What was this doing?
                //if (renamedFile.StartsWith("_il"))
                //{
                //    renamedFile = string.Concat(renamedFile.AsSpan(3), "_il");
                //}
                renamedFile = RenameFileBase(renamedFile, "_speed_dbg", "_do");
                renamedFile = RenameFileBase(renamedFile, "_speed_rel", "_ro");
                renamedFile = RenameFileBase(renamedFile, "_opt_dbg", "_do");
                renamedFile = RenameFileBase(renamedFile, "_opt_rel", "_ro");
                renamedFile = RenameFileBase(renamedFile, "_odbg", "_do");
                renamedFile = RenameFileBase(renamedFile, "_orel", "_ro");
                renamedFile = RenameFileBase(renamedFile, "_dbg", "_d");
                renamedFile = RenameFileBase(renamedFile, "_rel", "_r");
                renamedFile = RenameFileBase(renamedFile, "-dbg", "_d");
                renamedFile = RenameFileBase(renamedFile, "-ret", "_r");
                if (testProject.IsILProject)
                {
                    renamedFile = RenameFileBase(renamedFile, "_d", "_il_d");
                    renamedFile = RenameFileBase(renamedFile, "_do", "_il_do");
                    renamedFile = RenameFileBase(renamedFile, "_r", "_il_r");
                    renamedFile = RenameFileBase(renamedFile, "_ro", "_il_ro");
                }

                // This doesn't really fit into UnifyDbgRelProjects but is about project renaming,
                // so it's here for now.
                if (testProject.IsILProject)
                {
                    TestProject.GetKeyNameRootNameAndSuffix(renamedFile, out _, out string rootName, out _);
                    string sourceRootName = Path.GetFileNameWithoutExtension(testProject.CompileFiles.Single());

                    if ((rootName != sourceRootName)
                        && string.Equals(rootName, sourceRootName, StringComparison.OrdinalIgnoreCase))
                    {
                        // HACK: If we have "foo.ilproj" and "Foo.il", we'll have trouble doing the
                        // case-sensitive rename of "Foo.il" to "foo.il", so we'll use "foo_.ilproj"
                        // instead and then the rename to foo_.il will work.

                        // And if we're going to this trouble anyway, see if the name has a case
                        // mismatch with the directory name.

                        string innerDirectoryName = Path.GetFileName(dir);
                        if ((renamedFile != innerDirectoryName)
                            && string.Equals(renamedFile, innerDirectoryName, StringComparison.OrdinalIgnoreCase))
                        {
                            renamedFile = innerDirectoryName;
                        }

                        renamedFile += "_";
                    }
                }

                if (renamedFile != file)
                {
                    string renamedPath = Path.Combine(dir, renamedFile + ext);
                    Utils.FileMove(testProject.AbsolutePath, renamedPath, overwrite: false);
                }
            }
        }

        private static string[] s_wrapperGroups = new string[] { "_do", "_ro", "_d", "_r", "" };

        // Side effects: Moves project files, sets NewAbsolutePath
        //
        // Note: This logic doesn't guarantee that two projects with the same root end up staying that way,
        // though this requires inconsistency in collisions.  For example:
        // [dir1\foo_d.csproj, dir1\foo_r.csproj,      dir1\foo_d.ilproj,    dir2\foo_r.csproj]
        // could end up as
        // [dir1\foo_d.csproj, dir1\foo_dir1_r,csproj, dir1\foo_il_d.ilproj, dir2\foo_dir2_r.csproj]
        // This could cause projects with different root names to share a source file.  A subsequent
        // run on ILTransform would likely detect this.
        public void DeduplicateProjectNames()
        {
            foreach (string wrapperGroup in s_wrapperGroups)
            {
                Dictionary<string, List<TestProject>> rootNameToProjectMap = new Dictionary<string, List<TestProject>>();

                foreach (TestProject testProject in _projects)
                {
                    string projectName = Path.GetFileNameWithoutExtension(testProject.RelativePath);
                    if (wrapperGroup != "")
                    {
                        if (!projectName.EndsWith(wrapperGroup))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        if (s_wrapperGroups.Any(wg => wg != "" && projectName.EndsWith(wg)))
                        {
                            continue;
                        }
                    }

                    testProject.GetKeyNameRootNameAndSuffix(out string keyName, out _, out _);
                    Utils.AddToMultiMap(rootNameToProjectMap, keyName, testProject);
                }

                foreach (List<TestProject> projectList in rootNameToProjectMap.Values.Where(pl => pl.Count > 1))
                {
                    List<string> projectDirs = projectList.Select(p => Path.GetDirectoryName(p.AbsolutePath)).ToList()!;
                    List<string> extraRootNames;

                    // Simple case: sometest.csproj and sometest.ilproj => add _il to the ilproj
                    int ilprojIndex;
                    if ((projectList.Count == 2)
                        && (Path.GetFileNameWithoutExtension(projectList[0].RelativePath) == Path.GetFileNameWithoutExtension(projectList[1].RelativePath))
                        && (projectList.FindIndex(p => Path.GetExtension(p.RelativePath) == ".csproj") != -1)
                        && ((ilprojIndex = projectList.FindIndex(p => Path.GetExtension(p.RelativePath) == ".ilproj")) != -1))
                    {
                        extraRootNames = new List<string> {
                            ilprojIndex == 0 ? "il" : "",
                            ilprojIndex == 1 ? "il" : ""
                        };
                    }
                    else
                    {
                        List<string>? differences = Utils.GetNearestDirectoryWithDifferences(projectList.Select(p => p.AbsolutePath).ToList());
                        if (differences == null)
                        {
                            Console.WriteLine("No collision found for duplicate project names:");
                            projectList.ForEach(p => Console.WriteLine($"  {p.AbsolutePath}"));
                            continue;
                        }
                        extraRootNames = Utils.TrimSharedTokens(differences!);
                    }

                    foreach ((TestProject project, string projectDir, string extraRootName)
                        in projectList.Zip(projectDirs, extraRootNames))
                    {
                        project.GetKeyNameRootNameAndSuffix(out _, out string rootName, out string suffix);

                        // If the directory name matches the project name, then don't create the duplicate (e.g.,foo_foo.csproj)
                        if ((rootName == extraRootName) || (string.IsNullOrEmpty(extraRootName)))
                        {
                            continue;
                        }

                        string newRootName = rootName + "_" + extraRootName;
                        string newProjectPath = Path.Combine(projectDir, newRootName + suffix);

                        project.NewAbsolutePath = newProjectPath;
                        Utils.FileMove(project.AbsolutePath, newProjectPath, overwrite: false);
                    }
                }
            }
        }

        // Side effects: renames <NewTestClassSourceFile>, sets NewTestClassSourceFile and CompileFiles[0]
        public void FixILFileNames()
        {
            foreach (TestProject testProject in _projects)
            {
                if (!testProject.IsILProject) continue;
                if (string.IsNullOrEmpty(testProject.MainClassSourceFile)) continue;

                string projectName = Path.GetFileNameWithoutExtension(testProject.RelativePath);

                string dir = Path.GetDirectoryName(testProject.MainClassSourceFile)!;
                string rootName = Path.GetFileNameWithoutExtension(testProject.MainClassSourceFile);
                string extension = Path.GetExtension(testProject.MainClassSourceFile); // should be .il

                TestProject.GetKeyNameRootNameAndSuffix(
                    testProject.NewAbsolutePath ?? testProject.AbsolutePath,
                    out _,
                    out string projectRootName,
                    out _);
                string newSourceFile = string.Concat(dir, Path.DirectorySeparatorChar, projectRootName, extension);

                if (rootName != projectRootName)
                {
                    if (_movedFiles.TryAdd(testProject.MainClassSourceFile, newSourceFile))
                    {
                        Utils.FileMove(testProject.MainClassSourceFile, newSourceFile);
                    }
                    else
                    {
                        // Already moved this file. Check that the targets match.
                        string prevCopy = _movedFiles[testProject.MainClassSourceFile];
                        if (newSourceFile != prevCopy)
                        {
                            Console.WriteLine($"Conflict in moving {testProject.MainClassSourceFile}");
                            Console.WriteLine($"to {newSourceFile}");
                            Console.WriteLine($"and {prevCopy}");
                        }
                    }
                    testProject.NewTestClassSourceFile = newSourceFile;
                    testProject.CompileFiles[0] = newSourceFile;
                }
            }

        }

        // Side effects: Creates cs/csproj files
        public void GenerateAllWrappers(string outputDir)
        {
            HashSet<DebugOptimize> debugOptimizeMap = new HashSet<DebugOptimize>();
            foreach (TestProject testProject in _projects)
            {
                debugOptimizeMap.Add(testProject.DebugOptimize);
            }
            foreach (DebugOptimize debugOpt in debugOptimizeMap.OrderBy(d => d))
            {
                GenerateWrapper(outputDir, debugOpt, maxProjectsPerWrapper: 100);
            }
        }

        // Side effects: Creates cs/csproj files
        private void GenerateWrapper(string rootDir, DebugOptimize debugOptimize, int maxProjectsPerWrapper)
        {
            string dbgOptName = "Dbg" + debugOptimize.Debug + "_Opt" + debugOptimize.Optimize;
            string outputDir = Path.Combine(rootDir, dbgOptName);

            Directory.CreateDirectory(outputDir);

            foreach (string preexistingFile in Directory.GetFiles(outputDir))
            {
                File.Delete(preexistingFile);
            }

            TestProject[] projects = _projects.Where(p => p.DebugOptimize.Equals(debugOptimize)).ToArray();
            for (int firstProject = 0; firstProject < projects.Length; firstProject += maxProjectsPerWrapper)
            {
                string nameBase = dbgOptName;
                if (projects.Length > maxProjectsPerWrapper)
                {
                    nameBase += $"_{firstProject}";
                }

                TestProject[] projectGroup = projects[firstProject..Math.Min(projects.Length, firstProject + maxProjectsPerWrapper)];

                string wrapperSourceName = nameBase + ".cs";
                string wrapperSourcePath = Path.Combine(outputDir, wrapperSourceName);

                string wrapperProjectName = nameBase + ".csproj";
                string wrapperProjectPath = Path.Combine(outputDir, wrapperProjectName);

                using (StreamWriter writer = new StreamWriter(wrapperSourcePath))
                {
                    foreach (TestProject project in projectGroup.Where(p => p.MainClassName != ""))
                    {
                        writer.WriteLine("extern alias " + project.TestProjectAlias + ";");
                    }
                    writer.WriteLine();

                    writer.WriteLine("using System;");
                    writer.WriteLine();

                    writer.WriteLine("public static class " + dbgOptName);
                    writer.WriteLine("{");
                    writer.WriteLine("    private static int s_passed = 0;");
                    writer.WriteLine("    private static int s_noClass = 0;");
                    writer.WriteLine("    private static int s_exitCode = 0;");
                    writer.WriteLine("    private static int s_crashed = 0;");
                    writer.WriteLine("    private static int s_total = 0;");
                    writer.WriteLine();
                    writer.WriteLine("    public static int Main(string[] args)");
                    writer.WriteLine("    {");

                    foreach (TestProject project in projectGroup)
                    {
                        string testName = project.RelativePath.Replace('\\', '/');
                        if (project.MainClassName != "")
                        {
                            writer.WriteLine("        TryTest(\"" + testName + "\", " + project.TestProjectAlias + "::" + project.MainClassName + ".TestEntryPoint, args);");
                        }
                        else
                        {
                            writer.WriteLine("        Console.WriteLine(\"Skipping test: '" + testName + "' - no class name\");");
                            writer.WriteLine("        s_total++;");
                            writer.WriteLine("        s_noClass++;");
                        }
                    }

                    writer.WriteLine("        Console.WriteLine(\"Total tests: {0}; {1} passed; {2} missing class name; {3} returned wrong exit code; {4} crashed\", s_total, s_passed, s_noClass, s_exitCode, s_crashed);");
                    writer.WriteLine("        return s_crashed != 0 ? 1 : s_exitCode != 0 ? 2 : 100;");
                    writer.WriteLine("    }");
                    writer.WriteLine();
                    writer.WriteLine("    private static void TryTest(string testName, Func<string[], int> testFn, string[] args)");
                    writer.WriteLine("    {");
                    writer.WriteLine("        try");
                    writer.WriteLine("        {");
                    writer.WriteLine("            s_total++;");
                    writer.WriteLine("            int exitCode = testFn(args);");
                    writer.WriteLine("            if (exitCode == 100)");
                    writer.WriteLine("            {");
                    writer.WriteLine("                Console.WriteLine(\"Test succeeded: '{0}'\", testName);");
                    writer.WriteLine("                s_passed++;");
                    writer.WriteLine("            }");
                    writer.WriteLine("            else");
                    writer.WriteLine("            {");
                    writer.WriteLine("                Console.Error.WriteLine(\"Wrong exit code: '{0}' - {1}\", testName, exitCode);");
                    writer.WriteLine("                s_exitCode++;");
                    writer.WriteLine("            }");
                    writer.WriteLine("        }");
                    writer.WriteLine("        catch (Exception ex)");
                    writer.WriteLine("        {");
                    writer.WriteLine("            Console.Error.WriteLine(\"Test crashed: '{0}' - {1}\", testName, ex.Message);");
                    writer.WriteLine("            s_crashed++;");
                    writer.WriteLine("        }");
                    writer.WriteLine("    }");
                    writer.WriteLine("}");
                }

                using (StreamWriter writer = new StreamWriter(wrapperProjectPath))
                {
                    writer.WriteLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
                    writer.WriteLine("    <PropertyGroup>");
                    writer.WriteLine("        <OutputType>Exe</OutputType>");
                    writer.WriteLine("        <CLRTestKind>BuildAndRun</CLRTestKind>");
                    writer.WriteLine("    </PropertyGroup>");
                    writer.WriteLine("    <ItemGroup>");
                    writer.WriteLine("        <Compile Include=\"" + wrapperSourceName + "\" />");
                    writer.WriteLine("    </ItemGroup>");
                    writer.WriteLine("    <ItemGroup>");
                    HashSet<string> transitiveDependencies = new HashSet<string>();
                    foreach (TestProject project in projectGroup)
                    {
                        string relativePath = Path.GetRelativePath(outputDir, project.AbsolutePath);
                        writer.WriteLine("        <ProjectReference Include=\"" + relativePath + "\" Aliases=\"" + project.TestProjectAlias + "\" />");
                        transitiveDependencies.UnionWith(project.ProjectReferences);
                    }
                    foreach (string transitiveDependency in transitiveDependencies)
                    {
                        string relativePath = Path.GetRelativePath(outputDir, transitiveDependency);
                        writer.WriteLine("        <ProjectReference Include=\"" + relativePath + "\" />");
                    }

                    writer.WriteLine("    </ItemGroup>");
                    writer.WriteLine("</Project>");
                }
            }
        }

        // Side effect: Add TestProjects to _projects
        private void ScanRecursive(string absolutePath, string relativePath, ref int projectCount)
        {
            foreach (string absoluteProjectPath in Directory.EnumerateFiles(absolutePath, "*.*proj", SearchOption.TopDirectoryOnly))
            {
                string relativeProjectPath = Path.Combine(relativePath, Path.GetFileName(absoluteProjectPath));
                ScanProject(absoluteProjectPath, relativeProjectPath);
                if (++projectCount % 500 == 0)
                {
                    Console.WriteLine("Projects scanned: {0}", projectCount);
                }
            }
            foreach (string absoluteSubdirectoryPath in Directory.EnumerateDirectories(absolutePath, "*", SearchOption.TopDirectoryOnly))
            {
                string relativeSubdirectoryPath = Path.Combine(relativePath, Path.GetFileName(absoluteSubdirectoryPath));
                ScanRecursive(absoluteSubdirectoryPath, relativeSubdirectoryPath, ref projectCount);
            }
        }

        // Side effect: Adds a TestProject to _projects
        private void ScanProject(string absolutePath, string relativePath)
        {
            string projectName = Path.GetFileNameWithoutExtension(relativePath);
            string projectDir = Path.GetDirectoryName(absolutePath)!;

            List<string> compileFiles = new List<string>();
            bool compileFilesIncludeProjectName = false;
            List<string> projectReferences = new List<string>();
            Dictionary<string, string> allProperties = new Dictionary<string, string>();
            HashSet<string> allItemGroups = new HashSet<string>();

            XmlDocument xmlDoc = new XmlDocument();
            xmlDoc.Load(absolutePath);
            foreach (XmlNode project in xmlDoc.GetElementsByTagName("Project"))
            {
                foreach (XmlNode projectChild in project.ChildNodes)
                {
                    if (projectChild.Name == "PropertyGroup")
                    {
                        foreach (XmlNode property in projectChild.ChildNodes)
                        {
                            if (property.Name != "#comment")
                            {
                                allProperties[property.Name] = property.InnerText;
                            }
                        }
                    }
                    else if (projectChild.Name == "ItemGroup")
                    {
                        foreach (XmlNode item in projectChild.ChildNodes)
                        {
                            if (item.Name != "#comment")
                            {
                                allItemGroups.Add(item.Name);
                            }

                            switch (item.Name)
                            {
                                case "Compile":
                                    {
                                        string? compileFileList = item.Attributes?["Include"]?.Value;
                                        if (compileFileList is not null)
                                        {
                                            string[] compileFileArray = compileFileList.Split(' ');
                                            foreach (string compileFile in compileFileArray)
                                            {
                                                if (compileFile.Contains("$(MSBuildProjectName)"))
                                                {
                                                    compileFilesIncludeProjectName = true;
                                                }
                                                string file = compileFile
                                                    .Replace("$(MSBuildProjectName)", projectName)
                                                    .Replace("$(MSBuildThisFileName)", projectName)
                                                    .Replace("$(InteropCommonDir)", "../common/"); // special case for src\tests\Interop\...
                                                compileFiles.Add(Path.GetFullPath(file, projectDir));
                                            }
                                        }
                                    }
                                    break;

                                case "ProjectReference":
                                    {
                                        string? projectReference = item.Attributes?["Include"]?.Value;
                                        if (projectReference is not null)
                                        {
                                            projectReferences.Add(Path.GetFullPath(projectReference, projectDir));
                                        }
                                    }
                                    break;
                            }
                        }
                    }
                }
            }

            SourceInfo sourceInfo = new SourceInfo();
            foreach (string compileFile in compileFiles)
            {
                try
                {
                    AnalyzeSource(compileFile, ref sourceInfo);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Error analyzing '{0}': {1}", compileFile, ex);
                }
            }

            _projects.Add(new TestProject(
                absolutePath: absolutePath,
                relativePath: relativePath,
                allProperties: allProperties,
                allItemGroups: allItemGroups,
                compileFiles: compileFiles.ToArray(),
                compileFilesIncludeProjectName: compileFilesIncludeProjectName,
                projectReferences: projectReferences.ToArray(),
                sourceInfo: sourceInfo));
        }

        private static void AnalyzeSource(
            string path,
            ref SourceInfo sourceInfo)
        {
            if (path.IndexOf('*') < 0 && path.IndexOf('?') < 0)
            {
                // Exact path
                AnalyzeFileSource(path, ref sourceInfo);
                return;
            }

            string directory = Path.GetDirectoryName(path)!;
            string pattern = Path.GetFileName(path);
            SearchOption searchOption = SearchOption.TopDirectoryOnly;
            if (Path.GetFileName(directory) == "**")
            {
                searchOption = SearchOption.AllDirectories;
                directory = Path.GetDirectoryName(directory)!;
            }
            else if (pattern == "**")
            {
                searchOption = SearchOption.AllDirectories;
                pattern = "*";
            }

            foreach (string file in Directory.EnumerateFiles(directory, pattern, searchOption))
            {
                AnalyzeFileSource(file, ref sourceInfo);
            }
        }

        private static void AnalyzeFileSource(
            string path,
            ref SourceInfo sourceInfo)
        {
            switch (Path.GetExtension(path).ToLower())
            {
                case ".il":
                    AnalyzeILSource(path, ref sourceInfo);
                    break;

                case ".cs":
                    AnalyzeCSSource(path, ref sourceInfo);
                    break;

                default:
                    Console.Error.WriteLine("Cannot analyze source file '{0}'", path);
                    break;
            }
        }

        private static int GetIndent(string line) => line.SkipWhiteSpace();

        private static readonly string[] MethodModifierStrings = new string[] { "public", "private", "internal", "unsafe", "static" };

        private static void AnalyzeCSSource(string path, ref SourceInfo sourceInfo)
        {
            List<string> lines = new List<string>(File.ReadAllLines(path));

            if (Path.GetFileName(path).ToLower() == "expl_obj_1.cs")
            {
                Console.WriteLine("AnalyzeCSSource: {0}", path);
            }

            if (lines.Any(line => line.Contains("Environment.Exit")))
            {
                sourceInfo.HasExit = true;
            }

            string currentNamespace = "";
            for (int lineNumber = 0; lineNumber < lines.Count; ++lineNumber)
            {
                string line = lines[lineNumber];
                if (TryGetCSNamespaceName(line, out string namespaceName))
                {
                    if (string.IsNullOrEmpty(currentNamespace))
                    {
                        currentNamespace = namespaceName;
                    }
                    else
                    {
                        Console.WriteLine("Two namespaces in {0}", path); // need to think about this case
                        currentNamespace = currentNamespace + "." + namespaceName;
                    }
                    continue;
                }

                if (TryGetCSTypeName(path, line, out string typeName, out _))
                {
                    if (!string.IsNullOrEmpty(currentNamespace))
                    {
                        typeName = currentNamespace + "." + typeName;
                    }

                    sourceInfo.TypeNames.Add(typeName);
                }
            }

            bool isMainFile = false;
            if (lines.Any(line => line.Contains("[Fact]") || line.Contains("[ConditionalFact]")))
            {
                sourceInfo.HasFactAttribute = true;
                isMainFile = true;
            }

            for (int mainLine = lines.Count; --mainLine >= 0;)
            {
                string line = lines[mainLine];

                bool foundEntryPoint = false;
                if (line.IndicesOf("int Main()", out int mainColumnStart, out int mainColumnEnd)
                    || line.IndicesOf("void Main()", out mainColumnStart, out mainColumnEnd))
                {
                    sourceInfo.MainMethodName = "Main";
                    foundEntryPoint = true;
                }
                else if (line.IndicesOf("int TestEntryPoint()", out mainColumnStart, out mainColumnEnd)
                    || line.IndicesOf("void TestEntryPoint()", out mainColumnStart, out mainColumnEnd))
                {
                    sourceInfo.MainMethodName = "TestEntryPoint";
                    foundEntryPoint = true;
                }

                if (!foundEntryPoint)
                {
                    continue;
                }

                int firstMainLine = mainLine;
                int firstMainColumn = mainColumnStart;

                (int searchLine, int searchColumn) = lines.ReverseSkipWhiteSpace(mainLine, mainColumnStart - 1);
                bool found;
                do
                {
                    found = false;
                    foreach (string modifier in MethodModifierStrings)
                    {
                        if (lines[searchLine].AsSpan(0, searchColumn + 1).EndsWith(modifier))
                        {
                            found = true;
                            firstMainLine = searchLine;
                            firstMainColumn = searchColumn - modifier.Length;
                            (searchLine, searchColumn) = lines.ReverseSkipWhiteSpace(firstMainLine, firstMainColumn - 1);
                        }
                    }
                }
                while (found);
                if (lines[firstMainLine].ReverseSkipWhiteSpace(firstMainColumn - 1) != -1)
                {
                    Console.WriteLine("Found unexpected non-whitespace in Main/TestEntryPoint declaration");
                    Console.WriteLine("{0}:{1}", path, firstMainLine);
                    Console.WriteLine(lines[firstMainLine]);
                    continue;
                }

                int mainLineIndent = GetIndent(line);

                // First is reasonable but not perfect.  Last isn't accurate here.
                sourceInfo.FirstMainMethodDefLine = firstMainLine;
                sourceInfo.MainTokenMethodLine = mainLine;
                sourceInfo.LastMainMethodDefLine = mainLine;

                isMainFile = true;
                sourceInfo.MainClassSourceFile = path;
                while (--mainLine >= 0)
                {
                    line = lines[mainLine];
                    int lineIndent = GetIndent(line);
                    if (lineIndent < mainLineIndent && line.Contains('{'))
                    {
                        do
                        {
                            line = lines[mainLine];
                            if (TryGetCSTypeName(path, line, out string typeName, out int typeNameEnd))
                            {
                                sourceInfo.MainClassName = typeName;
                                sourceInfo.MainClassLine = mainLine;

                                int basePos = line.SkipWhiteSpace(typeNameEnd);
                                if (basePos < line.Length && line[basePos] == ':')
                                {
                                    basePos++;
                                    while (basePos < line.Length && line[basePos] != '{')
                                    {
                                        if (char.IsWhiteSpace(line[basePos]) || line[basePos] == ',')
                                        {
                                            basePos++;
                                            continue;
                                        }
                                        int baseIdentBegin = basePos;
                                        while (basePos < line.Length && TestProject.IsIdentifier(line[basePos]))
                                        {
                                            basePos++;
                                        }

                                        // For a generic (see code below for parsing <TArgs>), only store the base name.
                                        if (basePos > baseIdentBegin)
                                        {
                                            sourceInfo.MainClassBases.Add(line.Substring(baseIdentBegin, basePos - baseIdentBegin));
                                        }

                                        if (basePos < line.Length && line[basePos] == '<')
                                        {
                                            int genericNesting = 1;
                                            basePos++;
                                            while (basePos < line.Length)
                                            {
                                                char c = line[basePos++];
                                                if (c == '<')
                                                {
                                                    genericNesting++;
                                                }
                                                else if (c == '>')
                                                {
                                                    if (--genericNesting == 0)
                                                    {
                                                        break;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }

                                while (--mainLine >= 0)
                                {
                                    line = lines[mainLine];
                                    if (TryGetCSNamespaceName(line, out string namespaceName))
                                    {
                                        sourceInfo.MainClassName = namespaceName + "." + sourceInfo.MainClassName;
                                    }
                                }
                            }
                        }
                        while (--mainLine >= 0);
                    }
                }
                break;
            }

            if (isMainFile)
            {
                {
                    sourceInfo.LastHeaderCommentLine = -1;
                    int lineIndex;
                    for (lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                    {
                        string line = lines[lineIndex].TrimStart();
                        if (!line.StartsWith("//"))
                        {
                            break;
                        }
                        sourceInfo.LastHeaderCommentLine = lineIndex;
                    }
                    if (lineIndex < lines.Count)
                    {
                        if (string.IsNullOrWhiteSpace(lines[lineIndex]))
                        {
                            sourceInfo.LastHeaderCommentLine = lineIndex;
                        }
                    }
                }

                sourceInfo.LastUsingLine = -1;
                for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                {
                    string line = lines[lineIndex];
                    if (line.StartsWith("using"))
                    {
                        sourceInfo.LastUsingLine = lineIndex;
                    }
                }

                for (int lineIndex = sourceInfo.LastUsingLine + 1; lineIndex < lines.Count; lineIndex++)
                {
                    string line = lines[lineIndex].Trim();
                    if (line == "" || line.StartsWith("//"))
                    {
                        continue;
                    }
                    // Should also skip multi-line /* */ here
                    sourceInfo.NamespaceLine = lineIndex;
                    if (TryGetCSNamespaceName(line, out string namespaceName))
                    {
                        sourceInfo.NamespaceIdentLine = lineIndex;
                        sourceInfo.MainClassNamespace = namespaceName;
                    }
                    break;
                }
            }
        }

        private static bool TryGetCSTypeName(string path, string line, out string typeName, out int typeNameEnd)
        {
            int commentStart = line.IndexOf("//");
            if (commentStart == -1) commentStart = line.Length;

            if (!line.IndicesOf("class ", 0, commentStart, out int typeKeywordIndex, out int typeNameStart)
                && !line.IndicesOf("struct ", 0, commentStart, out typeKeywordIndex, out typeNameStart))
            {
                typeName = "";
                typeNameEnd = -1;
                return false;
            }
            if (typeKeywordIndex != 0)
            {
                int beforeKeyword = typeKeywordIndex - 1;
                int precedingIndex = line.ReverseSkipWhiteSpace(beforeKeyword);
                if ((precedingIndex == beforeKeyword) || // no whitespace before keyword
                    (precedingIndex >= 0 && line[precedingIndex] == ':')) // where T : class
                {
                    typeName = "";
                    typeNameEnd = -1;
                    return false;
                }
            }

            typeNameStart = line.SkipWhiteSpace(typeNameStart);
            int searchTypeNameEnd = typeNameStart;
            while (searchTypeNameEnd < line.Length && TestProject.IsIdentifier(line[searchTypeNameEnd]))
            {
                searchTypeNameEnd++;
            }

            if (typeNameStart == searchTypeNameEnd)
            {
                typeName = "";
                typeNameEnd = -1;
                Console.WriteLine("Found C# class/struct with no type name in {0}:", path);
                Console.WriteLine("  {0}", line);
                return false;
            }

            typeName = line.Substring(typeNameStart, searchTypeNameEnd - typeNameStart);
            typeNameEnd = searchTypeNameEnd;
            return true;
        }

        private static bool TryGetCSNamespaceName(string line, out string namespaceName)
        {
            int namespaceNameStart = line.EndIndexOf("namespace ");
            if (namespaceNameStart < 0)
            {
                namespaceName = "";
                return false;
            }

            int namespaceNameEnd = namespaceNameStart;
            while (namespaceNameEnd < line.Length && TestProject.IsIdentifier(line[namespaceNameEnd]))
            {
                namespaceNameEnd++;
            }

            namespaceName = line.Substring(namespaceNameStart, namespaceNameEnd - namespaceNameStart);
            return true;
        }

        private static void AnalyzeILSource(string path, ref SourceInfo sourceInfo)
        {
            if (Path.GetFileName(path) == "han3.il")
            {
                Console.WriteLine("AnalyzeILSource: {0}", path);
            }

            List<string> lines = new List<string>(File.ReadAllLines(path));

            if (lines.Any(line => line.Contains("Environment::Exit")))
            {
                sourceInfo.HasExit = true;
            }

            AnalyzeILSourceForTypeNames(path, lines, ref sourceInfo);
            AnalyzeILSourceForEntryPoint(path, lines, ref sourceInfo);

            if (sourceInfo.NamespaceLine < 0)
            {
                int index = lines.FindIndex(line => line.Contains(".class") || line.Contains(".struct"));
                if (index >= 0)
                {
                    sourceInfo.NamespaceLine = index;
                }
            }

            // IL projects don't actually need the Fact attribute providing they have a Main method
            sourceInfo.HasFactAttribute = (sourceInfo.FirstMainMethodDefLine >= 0);
        }

        private static void AnalyzeILSourceForTypeNames(string path, List<string> lines, ref SourceInfo sourceInfo)
        {
            string currentNamespace = "";
            for (int lineNumber = 0; lineNumber < lines.Count; ++lineNumber)
            {
                string line = lines[lineNumber];
                if (TestProject.TryGetILNamespaceName(path, line, out string namespaceName))
                {
                    if (string.IsNullOrEmpty(currentNamespace))
                    {
                        currentNamespace = namespaceName;
                    }
                    else
                    {
                        Console.WriteLine("Two namespaces in {0}", path); // need to think about this case
                        currentNamespace = currentNamespace + "." + namespaceName;
                    }
                    continue;
                }

                if (TestProject.TryGetILTypeName(path, lines, lineNumber, out string typeName))
                {
                    if (!string.IsNullOrEmpty(currentNamespace))
                    {
                        typeName = currentNamespace + "." + typeName;
                    }

                    sourceInfo.TypeNames.Add(typeName);
                }
            }
        }

        private static void AnalyzeILSourceForEntryPoint(string path, List<string> lines, ref SourceInfo sourceInfo)
        {
            int lineIndex = lines.FindLastIndex(line => line.Contains(".entrypoint"));
            if (lineIndex == -1)
            {
                return;
            }

            if (lines.FindLastIndex(lineIndex - 1, line => line.Contains(".entrypoint")) >= 0)
            {
                Console.WriteLine("Found two .entrypoints in {0}", path);
                return;
            }

            lineIndex = lines.FindLastIndex(lineIndex, line => line.Contains(".method"));
            if (lines.FindLastIndex(lineIndex - 1, line => line.Contains(".entrypoint")) >= 0)
            {
                Console.WriteLine("Couldn't find .method for .entrypoint in {0}", path);
                return;
            }

            string line = lines[lineIndex];
            int column = 0;

            Regex skipRegex = new Regex(@"^\s*(?://.*)?$");
            Regex[] mainRegexes =
            {
                new Regex(@"\.method(?:\s+(?:/\*06000002\*/|public|private|privatescope|assembly|hidebysig))*\s+static"),
                new Regex(@"\s+(?:int32|unsigned\s+int32|void)(?:\s+modopt\(\[mscorlib\]System\.Runtime\.CompilerServices\.CallConvCdecl\))?"),
                new Regex(@"\s+(?<main>[^\s(]+)\s*\("),
                new Regex(@"\s*(?:(?<type>class\s+(?:\[(?:mscorlib|'mscorlib')\])?System\.String|string)\s*\[\s*\](?:\s*(?<arg>[0-9a-zA-z_]+))?|(?<arg>[0-9a-zA-z_]+))?"),
                new Regex(@"\s*\)(?:\s*c?il)?(?:\s*managed)?(?:\s*noinlining)?(?:\s*forwardref)?"),
                new Regex(@"\s*{"),
            };
            const int mainStartRegexIndex = 0;
            const int mainNameRegexIndex = 2;
            const int mainEndRegexIndex = 4;
            const int mainBodyStartRegexIndex = 5;
            List<int> mainMatchLineNumbers = new List<int>();
            List<Match> mainMatches = new List<Match>();

            for (int regexIndex = 0; regexIndex < mainRegexes.Length; ++regexIndex)
            {
                while (skipRegex.Match(line.Substring(column)).Success)
                {
                    line = lines[++lineIndex];
                    column = 0;
                }
                Regex mainRegex = mainRegexes[regexIndex];
                Match mainMatch = mainRegex.Match(line, column);
                if (!mainMatch.Success)
                {
                    Console.WriteLine("Couldn't match RE #{0} for entrypoint on line {1} in {2}", regexIndex, lineIndex, path);
                    return;
                }
                mainMatchLineNumbers.Add(lineIndex);
                mainMatches.Add(mainMatch);
                column = mainMatch.Index + mainMatch.Length;
            }

            string mainName = mainMatches[mainNameRegexIndex].Groups["main"].Value;
            if (mainName[0] == '\'' && mainName[mainName.Length - 1] == '\'')
            {
                mainName = mainName.Substring(1, mainName.Length - 2);
            }

            sourceInfo.MainMethodName = mainName;
            sourceInfo.FirstMainMethodDefLine = mainMatchLineNumbers[mainStartRegexIndex];
            sourceInfo.MainTokenMethodLine = mainMatchLineNumbers[mainNameRegexIndex];
            sourceInfo.LastMainMethodDefLine = mainMatchLineNumbers[mainEndRegexIndex];
            if (lines.FindIndex(
                sourceInfo.LastMainMethodDefLine,
                Math.Min(10, lines.Count - sourceInfo.LastMainMethodDefLine),
                line => line.Contains("FactAttribute")) >= 0)
            {
                sourceInfo.HasFactAttribute = true;
            }

            int mainBodyStartLine = mainMatchLineNumbers[mainBodyStartRegexIndex];
            int mainBodyStartColumn = mainMatches[mainBodyStartRegexIndex].Index + mainMatches[mainBodyStartRegexIndex].Length;
            (sourceInfo.LastMainMethodBodyLine, sourceInfo.LastMainMethodBodyColumn) =
                FindCloseBrace(lines, mainBodyStartLine, mainBodyStartColumn);

            // Old code searched for TestEntryPoint( to identify an entry point,
            // but the above .entrypoint/.method/regex search is enough to know that we have one.

            sourceInfo.MainClassSourceFile = path;

            lineIndex = sourceInfo.FirstMainMethodDefLine;
            while (--lineIndex >= 0 && sourceInfo.MainClassName == "")
            {
                if (TestProject.TryGetILTypeName(path, lines, lineIndex, out string className))
                {
                    sourceInfo.MainClassName = className;
                    sourceInfo.MainClassLine = lineIndex;
                    while (--lineIndex >= 0)
                    {
                        if (TestProject.TryGetILNamespaceName(path, lines[lineIndex], out string namespaceName))
                        {
                            sourceInfo.MainClassNamespace = namespaceName;
                            sourceInfo.NamespaceLine = lineIndex;
                            sourceInfo.NamespaceIdentLine = lineIndex;
                            sourceInfo.MainClassName = namespaceName + "." + sourceInfo.MainClassName;
                        }
                     }
                     break;
                }
            }
        }

        internal static (int, int) FindCloseBrace(List<string> lines, int startLineNumber, int startColumn)
        {
            int openBraces = 1;
            int lineNumber = startLineNumber;
            int columnNumber = startColumn;
            bool inQuote = false;
            bool inCommentBlock = false;

            string line = lines[lineNumber];

            for (;;)
            {
                while (columnNumber == line.Length)
                {
                    if (++lineNumber == lines.Count)
                    {
                        Console.WriteLine("Ran out of lines searching for }");
                        return (lineNumber - 1, lines[lineNumber - 1].Length);
                    }

                    line = lines[lineNumber];
                    columnNumber = 0;
                }

                if (inCommentBlock)
                {
                    if (line.AsSpan(columnNumber).StartsWith("*/"))
                    {
                        inCommentBlock = false;
                        columnNumber += 2;
                    }
                    else columnNumber++;
                }
                else if (inQuote)
                {
                    if (line[columnNumber] == '"')
                    {
                        inQuote = false;
                        columnNumber++;
                    }
                    else if (line.AsSpan(columnNumber).StartsWith("\\\""))
                    {
                        columnNumber += 2;
                    }
                    else columnNumber++;
                }
                else if (line[columnNumber] == '}')
                {
                    if (--openBraces == 0)
                    {
                        columnNumber = line.SkipWhiteSpace(columnNumber + 1);
                        if ((columnNumber != line.Length)
                            && line.AsSpan(columnNumber).StartsWith("//"))
                        {
                            columnNumber = line.Length;
                        }
                        return (lineNumber, columnNumber);
                    }

                    columnNumber++;
                }
                else if (line.AsSpan(columnNumber).StartsWith("//"))
                {
                    columnNumber = line.Length;
                }
                else if (line.AsSpan(columnNumber).StartsWith("/*"))
                {
                    inCommentBlock = true;
                    columnNumber += 2;
                }
                else if (line[columnNumber] == '"')
                {
                    inQuote = true;
                    columnNumber++;
                }
                else if (line[columnNumber] == '{')
                {
                    openBraces++;
                    columnNumber++;
                }
                else
                {
                    columnNumber++;
                }
            }
        }

        // Side effects: Adds to _classNameMap, adds to _classNameDbgOptMap, sets DeduplicatedNamespaceName
        private void PopulateClassNameMap()
        {
            HashSet<string> ilNamespaceClasses = new HashSet<string>();
            Dictionary<string, HashSet<string>> compileFileToFolderNameMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            // class name -> project file path (WITHOUT suffixes like _d that reuse source files) -> TestProjects
            // - A collision occurs if the initial class name lookup yields multiple reduced paths
            // - Each inner list of projects is an _d, _o, etc., family
            Dictionary<string, Dictionary<string, List<TestProject>>> classNameRootProjectNameMap = new Dictionary<string, Dictionary<string, List<TestProject>>>();

            //foreach (TestProject project in _projects.Where(p => p.MainClassName != "" && !string.IsNullOrEmpty(p.MainMethodName)))
            foreach (TestProject project in _projects)
            {
                foreach (string typeName in project.TypeNames)
                {
                    Utils.AddToMultiMap(_classNameMap, typeName, project);
                    Utils.AddToNestedMultiMap(_classNameDbgOptMap, typeName, project.DebugOptimize, project);

                    project.GetKeyNameRootNameAndSuffix(out _, out string rootName, out _);
                    string projectWithoutSuffix = Path.Combine(
                        Path.GetDirectoryName(project.AbsolutePath)!,
                        rootName + Path.GetExtension(project.AbsolutePath));
                    Utils.AddToNestedMultiMap(classNameRootProjectNameMap, typeName, projectWithoutSuffix, project);
                }

                foreach (string file in project.CompileFiles)
                {
                    string fileName = Path.GetFileName(file);
                    string folderName = Path.GetFileName(Path.GetDirectoryName(file)!);
                    Utils.AddToMultiMap(compileFileToFolderNameMap, fileName, folderName);
                }
            }

            List<KeyValuePair<string, List<TestProject>>> projectGroups =
                classNameRootProjectNameMap // class name -> project file path(WITHOUT suffixes like _d that reuse source files) -> TestProjects
                    .Select(kvp => kvp.Value) // project file path(WITHOUT suffixes like _d that reuse source files) -> TestProjects
                    .Where(map => map.Count > 1) // Find conflicts
                    .SelectMany(d => d) // Flatten dictionaries
                    .ToList();

            List<(string, TestProject)> representativeProjects =
                projectGroups
                    .Select(kvp => (kvp.Key, kvp.Value[0])) // Pair key with representative value
                    .Distinct(new TestProjectPathEqualityComparer())
                    .ToList();

            // Note: lots of duplication with DeduplicateProjectNames?

            string InterestingNamespace(TestProject project)
            {
                string originalNamespace = project.MainClassNamespace;
                if (new string[] { "Test", "JitTest", "Repro", "DefaultNamespace" }.Contains(originalNamespace)
                    || originalNamespace.Length <= 1)
                {
                    originalNamespace = "";
                }
                else
                {
                    originalNamespace = originalNamespace + "_";
                }
                return originalNamespace;
            }

            List<string> filenameAttempt = representativeProjects.Select(p => Path.GetFileNameWithoutExtension(p.Item2.MainClassSourceFile)).ToList();
            List<string> projectAttempt = representativeProjects.Select(p => Path.GetFileNameWithoutExtension(p.Item1)).ToList();
            List<string>? dirAttempt = Utils.GetNearestDirectoryWithDifferences(representativeProjects.Select(p => p.Item2.AbsolutePath).ToList());
            if (dirAttempt != null)
            {
                dirAttempt = Utils.TrimSharedTokens(dirAttempt);
            }
            int filenameSize = filenameAttempt.AllUnique() ? filenameAttempt.Select(s => s.Length).Sum() : int.MaxValue;
            int projectSize = projectAttempt.AllUnique() ? projectAttempt.Select(s => s.Length).Sum() : int.MaxValue;
            int dirSize = (dirAttempt != null) && dirAttempt.AllUnique() ? dirAttempt.Select(s => s.Length).Sum() : int.MaxValue;

            int bestSize = Math.Min(filenameSize, Math.Min(projectSize, dirSize));
            if (bestSize == int.MaxValue)
            {
                Console.WriteLine("No simple namespace renames for projects");
                return;
            }

            List<string> bestAttempt =
                (bestSize == filenameSize) ? filenameAttempt
                : (bestSize == projectSize) ? projectAttempt
                : dirAttempt!;

            foreach ((TestProject project, string newNamespace) in representativeProjects.Select(p => p.Item2).Zip(bestAttempt))
            {
                project.DeduplicatedNamespaceName = TestProject.SanitizeIdentifier(InterestingNamespace(project) + newNamespace);
            }

            // Propagate DeduplicatedNamespaceName to the other projects with the same root
            // (_d, _r, etc.)
            foreach (List<TestProject> projects in projectGroups.Select(kvp => kvp.Value))
            {
                TestProject representativeProject = projects[0];
                Debug.Assert(!string.IsNullOrEmpty(representativeProject.DeduplicatedNamespaceName));

                foreach (TestProject project in projects.Skip(1))
                {
                    // A project might be in more than one group, so we can't just check for null
                    // but it should get the same dedup name.
                    Debug.Assert(project.DeduplicatedNamespaceName == null
                        || project.DeduplicatedNamespaceName == representativeProject.DeduplicatedNamespaceName);
                    project.DeduplicatedNamespaceName = representativeProject.DeduplicatedNamespaceName;
                }
            }

            /*
            foreach (List<KeyValuePair<string, List<TestProject>>> projectGroups
                in classNameRootProjectNameMap
                    .Select(kvp => kvp.Value)
                    .Where(map => map.Count > 1) // Find conflicts
                    .Select(d => d.ToList()))
            {
                // Each iteration of this loop is for a separate main class name.
                //
                // For each iteration, the outer list is a separate family of projects that share the same
                // "root" (so they are a _d, _o, etc., family with the same directory, root name, and suffix)
                //
                // Each outer and inner list is guaranteed to be non-empty.  We'll take a representative from
                // each group, calculate a unique name for each, and then apply that name to each member of
                // the group.
                //
                // For example, for the following projects, we could choose any of the names to distinguish them:
                // [
                //   [dir1\foo_d.csproj, dir1\foo_r.csproj], => ""   or "dir1" or "foo"
                //   [dir2\bar.ilproj]                       => "il" or "dir2" or "bar"
                // ]

                // Note: lots of duplication with DeduplicateProjectNames

                string originalNamespace = projectGroups[0].Value[0].MainClassNamespace;
                if (new string[] { "Test", "JitTest", "Repro", "DefaultNamespace" }.Contains(originalNamespace)
                    || originalNamespace.Length <= 1)
                {
                    originalNamespace = "";
                }
                else
                {
                    originalNamespace = originalNamespace + "_";
                }

                List<string> newNamespaces;

                List<string>? extensionAttempt = (originalNamespace != null) ? projectGroups.Select(g => g.Value[0].IsILProject ? "il" : "").ToList() : null;
                List<string> filenameAttempt = projectGroups.Select(g => Path.GetFileNameWithoutExtension(g.Value[0].MainClassSourceFile)).ToList();
                List<string> projectAttempt = projectGroups.Select(g => Path.GetFileNameWithoutExtension(g.Key)).ToList();
                List<string>? dirAttempt = Utils.GetNearestDirectoryWithDifferences(projectGroups.Select(g => g.Key).ToList());
                if (dirAttempt != null)
                {
                    dirAttempt = Utils.TrimSharedTokens(dirAttempt);
                }
                int extensionSize = (extensionAttempt != null) && extensionAttempt.AllUnique() ? extensionAttempt.Select(s => s.Length).Sum() : int.MaxValue;
                int filenameSize = filenameAttempt.AllUnique() ? filenameAttempt.Select(s => s.Length).Sum() : int.MaxValue;
                int projectSize = projectAttempt.AllUnique() ? projectAttempt.Select(s => s.Length).Sum() : int.MaxValue;
                int dirSize = (dirAttempt != null) && dirAttempt.AllUnique() ? dirAttempt.Select(s => s.Length).Sum() : int.MaxValue;

                int bestSize = Math.Min(extensionSize, Math.Min(filenameSize, Math.Min(projectSize, dirSize)));
                if (bestSize == int.MaxValue)
                {
                    Console.WriteLine("No simple namespace renames for projects with class {0}:", projectGroups[0].Value[0].MainClassName);
                    projectGroups.ForEach(g => g.Value.ForEach(p => Console.WriteLine("  {0}", p.AbsolutePath)));
                    continue;
                }

                List<string> bestAttempt =
                    (bestSize == extensionSize) ? extensionAttempt!
                    : (bestSize == filenameSize) ? filenameAttempt
                    : (bestSize == projectSize) ? projectAttempt
                    : dirAttempt!;
                newNamespaces = bestAttempt.Select(str => originalNamespace + str).ToList();

                foreach ((List<TestProject> projectList, string newNamespace) in projectGroups.Select(kvp => kvp.Value).Zip(newNamespaces))
                {
                    foreach (TestProject project in projectList)
                    {
                        project.DeduplicatedNamespaceName = TestProject.SanitizeIdentifier(newNamespace);
                    }
                }
            }
            */
        }
    }
}
