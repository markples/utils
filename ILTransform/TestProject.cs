// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
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

        public string TestClassName = "";
        public List<string> TestClassBases = new List<string>();
        public string TestClassNamespace = "";
        public string TestClassSourceFile = "";
        public int TestClassLine = -1;
        public string MainMethodName = "";
        public int FirstMainMethodLine = -1;
        public int MainTokenMethodLine = -1;
        public int LastMainMethodLine = -1;
        public int LastHeaderCommentLine = -1; // will include one blank after comments if it exists
        public int LastUsingLine = -1;
        public int NamespaceLine = -1;
        public bool HasFactAttribute = false;
        public bool HasExit = false;
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
        public string TestClassName => SourceInfo.TestClassName;
        public List<string> TestClassBases => SourceInfo.TestClassBases;
        public string TestClassNamespace => SourceInfo.TestClassNamespace;
        public string TestClassSourceFile => NewTestClassSourceFile ?? SourceInfo.TestClassSourceFile;
        public int TestClassLine => SourceInfo.TestClassLine;
        public string MainMethodName => SourceInfo.MainMethodName;
        public int FirstMainMethodLine => SourceInfo.FirstMainMethodLine;
        public int MainTokenMethodLine => SourceInfo.MainTokenMethodLine;
        public int LastMainMethodLine => SourceInfo.LastMainMethodLine;
        public int LastHeaderCommentLine => SourceInfo.LastHeaderCommentLine;
        public int LastUsingLine => SourceInfo.LastUsingLine;
        public int NamespaceLine => SourceInfo.NamespaceLine;
        public bool HasFactAttribute => SourceInfo.HasFactAttribute;
        public Dictionary<string, string> AllProperties;
        public HashSet<string> AllItemGroups;

        public readonly bool IsILProject;

        public string? TestProjectAlias;
        public string? DeduplicatedClassName;
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
            return char.IsDigit(c) || char.IsLetter(c) || c == '_' || c == '@' || c == '$';
        }

        private static Regex PublicRegex = new Regex(@"public\s+");
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
                int charIndex = 0;
                while (charIndex < line.Length && char.IsWhiteSpace(line[charIndex]))
                {
                    charIndex++;
                }
                if (isILTest)
                {
                    while (charIndex < line.Length && !char.IsWhiteSpace(line[charIndex]))
                    {
                        charIndex++;
                    }
                    if (charIndex < line.Length)
                    {
                        charIndex++;
                    }
                }
                line = string.Concat(line.AsSpan(0, charIndex), "public ", line.AsSpan(charIndex));
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

        public static bool GetILClassName(string line, out string? className)
        {
            int scanIndex = line.EndIndexOf(".class");
            if (scanIndex < 0)
            {
                scanIndex = line.EndIndexOf(".struct");
            }
            if (scanIndex < 0)
            {
                className = null;
                return false;
            }

            while (scanIndex < line.Length)
            {
                if (char.IsWhiteSpace(line[scanIndex]))
                {
                    scanIndex++;
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
                    className = line.Substring(identStart, scanIndex - identStart);
                    return true;
                }
                if (IsIdentifier(line[scanIndex]))
                {
                    int identStart = scanIndex;
                    while (++scanIndex < line.Length && IsIdentifier(line[scanIndex]))
                    {
                    }
                    className = line.Substring(identStart, scanIndex - identStart);
                    switch (className)
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
                            className = null;
                            return false;

                        default:
                            return true;
                    }
                }
                break; // parse error
            }
            className = null;
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
        private readonly Dictionary<string, Dictionary<DebugOptimize, List<TestProject>>> _namespaceNameMap;
        private readonly HashSet<string> _commonClassNames;
        private readonly HashSet<string> _rewrittenFiles;
        private readonly Dictionary<string, string> _movedFiles;

        public TestProjectStore()
        {
            _projects = new List<TestProject>();
            _classNameMap = new Dictionary<string, List<TestProject>>();
            _namespaceNameMap = new Dictionary<string, Dictionary<DebugOptimize, List<TestProject>>>();
            _commonClassNames = new HashSet<string>();
            _rewrittenFiles = new HashSet<string>();
            _movedFiles = new Dictionary<string, string>();
        }

        public void AddCommonClassName(string className) => _commonClassNames.Add(className);

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
            "ReferenceXUnitWrapperGenerator", //! VERIFY
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

                foreach (TestProject project in _projects.Where(p => p.TestClassName != ""))
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

        public void DumpMultiProjectSources(TextWriter writer)
        {
            Dictionary<string, Dictionary<DebugOptimize, List<TestProject>>> potentialDuplicateMap = new Dictionary<string, Dictionary<DebugOptimize, List<TestProject>>>();
            foreach (TestProject project in _projects)
            {
                foreach (string source in project.CompileFiles)
                {
                    Utils.AddToNestedMultiMap(potentialDuplicateMap, source, project);
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
            writer.WriteLine("{0,-9} | (total)", duplicateClassNames.Where(kvp => kvp.Value.Count > 1).Sum(kvp => kvp.Value.Count));

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
                    writer.WriteLine(project.AbsolutePath);
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

            _projects.Where(p => p.TestClassName != "" && !p.HasFactAttribute && !p.AddedFactAttribute)
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
            HashSet<string> classNameDuplicates = new HashSet<string>(_classNameMap.Where(kvp => kvp.Value.Count > 1).Select(kvp => kvp.Key));

            int index = 0;
            foreach (TestProject project in _projects)
            {
                if (!string.IsNullOrEmpty(settings.ClassToDeduplicate) && project.TestClassName != settings.ClassToDeduplicate)
                {
                    continue;
                }
                new ILRewriter(
                    project,
                    classNameDuplicates,
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
                    // First find closest directory level with different names
                    int depth = 1;
                    const int maxDepth = 3;
                    do
                    {
                        HashSet<string> folderCollisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        bool foundCollision = false;
                        foreach (TestProject project in projectList)
                        {
                            string projectDir = Path.GetDirectoryName(project.AbsolutePath)!;
                            string dirKey = "";
                            for (int i = 0; i < depth; i++)
                            {
                                dirKey += "/" + Path.GetFileName(projectDir);
                                projectDir = Path.GetDirectoryName(projectDir)!;
                            }
                            if (!folderCollisions.Add(dirKey))
                            {
                                foundCollision = true;
                                break;
                            }
                        }
                        if (!foundCollision)
                        {
                            break;
                        }
                    }
                    while (++depth <= maxDepth);

                    // Check that we found one
                    if (depth > maxDepth)
                    {
                        Console.WriteLine("No collision found for duplicate project names:");
                        projectList.ForEach(p => Console.WriteLine($"  {p.AbsolutePath}"));
                        continue;
                    }

                    List<string> projectDirs = projectList.Select(p => Path.GetDirectoryName(p.AbsolutePath)).ToList()!;
                    List<string> extraRootNames;

                    int ilprojIndex;
                    // Simple case: sometest.csproj and sometest.ilproj => add _il to the ilproj
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
                        // Extract the 'depth'th directory name
                        {
                            IEnumerable<string> extraRootNameEnum = projectDirs;
                            for (int i = 1; i < depth; ++i)
                            {
                                extraRootNameEnum = extraRootNameEnum.Select(n => Path.GetDirectoryName(n)!);
                            }
                            extraRootNameEnum = extraRootNameEnum.Select(n => Path.GetFileName(n));
                            extraRootNames = extraRootNameEnum.ToList()!;
                        }

                        // Reduce ["pre-A-post", pre-B-post"] to ["A", "B"]

                        // Strip matching leading characters - but only add token (by _ or -) boundaries
                        int minLength = extraRootNames.Select(n => n.Length).Min();
                        int leadingMatches = 0;
                        int leadingTokenMatches = 0;
                        while ((leadingMatches < minLength)
                            && extraRootNames.All(n => n[leadingMatches] == extraRootNames[0][leadingMatches]))
                        {
                            bool isFullToken = new char[] { '_', '-' }.Contains(extraRootNames[0][leadingMatches]);
                            leadingMatches++;
                            if (isFullToken) leadingTokenMatches = leadingMatches;
                        }
                        extraRootNames = extraRootNames.Select(n => n.Substring(leadingTokenMatches)).ToList();

                        // Strip matching trailing characters
                        minLength = extraRootNames.Select(n => n.Length).Min();
                        int trailingMatches = 0;
                        int trailingTokenMatches = 0;
                        while ((trailingMatches < minLength)
                            && extraRootNames.All(n => n[n.Length - trailingMatches - 1] == extraRootNames[0][extraRootNames[0].Length - trailingMatches - 1]))
                        {
                            bool isFullToken = new char[] { '_', '-' }.Contains(extraRootNames[0][leadingMatches]);
                            trailingMatches++;
                            if (isFullToken) trailingTokenMatches = trailingMatches;
                        }
                        extraRootNames = extraRootNames.Select(n => n.Substring(0, n.Length - trailingTokenMatches)).ToList();
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
                if (string.IsNullOrEmpty(testProject.TestClassSourceFile)) continue;

                string projectName = Path.GetFileNameWithoutExtension(testProject.RelativePath);

                string dir = Path.GetDirectoryName(testProject.TestClassSourceFile)!;
                string rootName = Path.GetFileNameWithoutExtension(testProject.TestClassSourceFile);
                string extension = Path.GetExtension(testProject.TestClassSourceFile); // should be .il

                TestProject.GetKeyNameRootNameAndSuffix(
                    testProject.NewAbsolutePath ?? testProject.AbsolutePath,
                    out _,
                    out string projectRootName,
                    out _);
                string newSourceFile = string.Concat(dir, Path.DirectorySeparatorChar, projectRootName, extension);

                if (rootName != projectRootName)
                {
                    if (_movedFiles.TryAdd(testProject.TestClassSourceFile, newSourceFile))
                    {
                        Utils.FileMove(testProject.TestClassSourceFile, newSourceFile);
                    }
                    else
                    {
                        // Already moved this file. Check that the targets match.
                        string prevCopy = _movedFiles[testProject.TestClassSourceFile];
                        if (newSourceFile != prevCopy)
                        {
                            Console.WriteLine($"Conflict in moving {testProject.TestClassSourceFile}");
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
                    foreach (TestProject project in projectGroup.Where(p => p.TestClassName != ""))
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
                        if (project.TestClassName != "")
                        {
                            writer.WriteLine("        TryTest(\"" + testName + "\", " + project.TestProjectAlias + "::" + project.TestClassName + ".TestEntrypoint, args);");
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

        private static int GetIndent(string line)
        {
            int indent = 0;
            while (indent < line.Length && char.IsWhiteSpace(line[indent]))
            {
                indent++;
            }
            return indent;
        }

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

            bool isMainFile = false;
            for (int mainLine = lines.Count; --mainLine >= 0;)
            {
                string line = lines[mainLine];
                if (line.Contains("[Fact]") || line.Contains("[ConditionalFact]"))
                {
                    sourceInfo.HasFactAttribute = true;
                    isMainFile = true;
                }

                bool foundEntryPoint = false;
                if (line.Contains("int Main()"))
                {
                    sourceInfo.MainMethodName = "Main";
                    foundEntryPoint = true;
                }
                else if (line.Contains("TestEntrypoint()"))
                {
                    sourceInfo.MainMethodName = "TestEntrypoint";
                    foundEntryPoint = true;
                }

                if (foundEntryPoint)
                {
                    int mainLineIndent = GetIndent(line);

                    // First and Last aren't accurate here
                    sourceInfo.FirstMainMethodLine = mainLine;
                    sourceInfo.MainTokenMethodLine = mainLine;
                    sourceInfo.LastMainMethodLine = mainLine;

                    isMainFile = true;
                    sourceInfo.TestClassSourceFile = path;
                    while (--mainLine >= 0)
                    {
                        line = lines[mainLine];
                        int lineIndent = GetIndent(line);
                        if (lineIndent < mainLineIndent && line.Contains('{'))
                        {
                            do
                            {
                                line = lines[mainLine];
                                int classNameStart = line.EndIndexOf("class ");
                                if (classNameStart < 0)
                                {
                                    classNameStart = line.EndIndexOf("struct ");
                                }
                                if (classNameStart >= 0)
                                {
                                    int classNameEnd = classNameStart;
                                    while (line.Length > classNameEnd && TestProject.IsIdentifier(line[classNameEnd]))
                                    {
                                        classNameEnd++;
                                    }
                                    sourceInfo.TestClassName = line.Substring(classNameStart, classNameEnd - classNameStart);
                                    sourceInfo.TestClassLine = mainLine;

                                    int basePos = classNameEnd;
                                    while (basePos < line.Length && char.IsWhiteSpace(line[basePos]))
                                    {
                                        basePos++;
                                    }
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
                                            if (basePos > baseIdentBegin)
                                            {
                                                sourceInfo.TestClassBases.Add(line.Substring(baseIdentBegin, basePos - baseIdentBegin));
                                            }
                                        }
                                    }

                                    while (--mainLine >= 0)
                                    {
                                        line = lines[mainLine];
                                        int namespaceNameStart = line.EndIndexOf("namespace ");
                                        if (namespaceNameStart >= 0)
                                        {
                                            int namespaceNameEnd = namespaceNameStart;
                                            while (namespaceNameEnd < line.Length && TestProject.IsIdentifier(line[namespaceNameEnd]))
                                            {
                                                namespaceNameEnd++;
                                            }
                                            string namespaceName = line.Substring(namespaceNameStart, namespaceNameEnd - namespaceNameStart);
                                            sourceInfo.TestClassName = namespaceName + "." + sourceInfo.TestClassName;
                                        }
                                    }
                                }
                            }
                            while (--mainLine >= 0);
                        }
                    }
                    break;
                }
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
                    sourceInfo.NamespaceLine = lineIndex;
                    if (line.StartsWith("namespace "))
                    {
                        int namespaceNameStart = 10;
                        int namespaceNameEnd = namespaceNameStart;
                        while (namespaceNameEnd < line.Length && TestProject.IsIdentifier(line[namespaceNameEnd]))
                        {
                            namespaceNameEnd++;
                        }
                        sourceInfo.TestClassNamespace = line.Substring(namespaceNameStart, namespaceNameEnd - namespaceNameStart);
                    }
                    break;
                }
            }
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
            sourceInfo.HasFactAttribute = (sourceInfo.FirstMainMethodLine >= 0);
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

            Regex[] mainRegexes =
            {
                new Regex(@"\.method(?:\s+(?:/\*06000002\*/|public|private|privatescope|assembly|hidebysig))*\s+static"),
                new Regex(@"\s+(?:int32|unsigned\s+int32|void)(?:\s+modopt\(\[mscorlib\]System\.Runtime\.CompilerServices\.CallConvCdecl\))?"),
                new Regex(@"\s+(?<main>[^\s(]+)\s*\("),
                new Regex(@"\s*(?:(?<type>class\s+(?:\[(?:mscorlib|'mscorlib')\])?System\.String|string)\s*\[\s*\](?:\s*(?<arg>[0-9a-zA-z_]+))?|(?<arg>[0-9a-zA-z_]+))?"),
                new Regex(@"\s*\)"),
            };
            const int mainStartRegexIndex = 0;
            const int mainNameRegexIndex = 2;
            const int mainEndRegexIndex = 4;
            List<int> mainMatchLineNumbers = new List<int>();
            List<Match> mainMatches = new List<Match>();

            for (int regexIndex = 0; regexIndex < mainRegexes.Length; ++regexIndex)
            {
                Regex mainRegex = mainRegexes[regexIndex];
                Match mainMatch = mainRegex.Match(line, column);
                if (line.Substring(column).All(c => char.IsWhiteSpace(c)))
                {
                    lineIndex++;
                    line = lines[lineIndex];
                    mainMatch = mainRegex.Match(line);
                }
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
            sourceInfo.FirstMainMethodLine = mainMatchLineNumbers[mainStartRegexIndex];
            sourceInfo.MainTokenMethodLine = mainMatchLineNumbers[mainNameRegexIndex];
            sourceInfo.LastMainMethodLine = mainMatchLineNumbers[mainEndRegexIndex];
            if (lines.FindIndex(
                sourceInfo.LastMainMethodLine,
                Math.Min(10, lines.Count - sourceInfo.LastMainMethodLine),
                line => line.Contains("FactAttribute")) >= 0)
            {
                sourceInfo.HasFactAttribute = true;
            }

            // Old code searched for TestEntryPoint( to identify an entry point,
            // but the above .entrypoint/.method/regex search is enough to know that we have one.

            sourceInfo.TestClassSourceFile = path;

            lineIndex = sourceInfo.FirstMainMethodLine;
            while (--lineIndex >= 0 && sourceInfo.TestClassName == "")
            {
                if (TestProject.GetILClassName(lines[lineIndex], out string? className))
                {
                    sourceInfo.TestClassName = className!;
                    sourceInfo.TestClassLine = lineIndex;
                    while (--lineIndex >= 0)
                    {
                        string[] components = lines[lineIndex].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (components.Length >= 2 && components[0] == ".namespace")
                        {
                            string namespaceName = components[1];
                            if (namespaceName.StartsWith("\'"))
                            {
                                namespaceName = namespaceName.Substring(1, namespaceName.Length - 2);
                            }
                            sourceInfo.TestClassNamespace = namespaceName;
                            sourceInfo.NamespaceLine = lineIndex;
                            sourceInfo.TestClassName = namespaceName + "." + sourceInfo.TestClassName;
                        }
                     }
                     break;
                }
            }
        }

        // Side effects: Adds to _classNameMap, adds to _namespaceNameMap, sets DeduplicatedNamespaceName
        private void PopulateClassNameMap()
        {
            HashSet<string> ilNamespaceClasses = new HashSet<string>();
            Dictionary<string, HashSet<string>> compileFileToFolderNameMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (TestProject project in _projects.Where(p => p.TestClassName != "" && !string.IsNullOrEmpty(p.MainMethodName)))
            {
                if (project.RelativePath.Contains("hfa_params"))
                {
                    Console.WriteLine("Project: {0}", project.AbsolutePath);
                }

                Utils.AddToMultiMap(_classNameMap, project.TestClassName, project);

                if (project.CompileFiles.Any(f => Path.GetFileNameWithoutExtension(f) == "accum"))
                {
                    Console.WriteLine("Project: {0}", project.AbsolutePath);
                }

                string namespaceClass = project.TestClassNamespace + "#" + project.TestClassName;
                Utils.AddToNestedMultiMap(_namespaceNameMap, namespaceClass, project);

                foreach (string file in project.CompileFiles)
                {
                    string fileName = Path.GetFileName(file);
                    string folderName = Path.GetFileName(Path.GetDirectoryName(file)!);
                    if (!compileFileToFolderNameMap.TryGetValue(fileName, out HashSet<string>? folders))
                    {
                        folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        compileFileToFolderNameMap.Add(fileName, folders);
                    }
                    folders.Add(folderName);
                }
            }

            /*
            foreach (List<TestProject> projectList in _classNameMap.Values.Where(v => v.Count > 1))
            {
                var found = new HashSet<DebugOptimize>();
                bool doDedup = false;
                foreach (TestProject project in projectList)
                {
                    // No longer using "*" for "fake" - instead should check _commonClassNames if trying to use this again
                    if (project.DebugOptimize.Debug == "*" || !found.Add(project.DebugOptimize))
                    {
                       doDedup = true;
                       break;
                    }
                }

                if (doDedup)
                {
                    foreach (TestProject project in projectList)
                    {
                        project.DeduplicatedClassName = TestProject.SanitizeIdentifier(project.TestClassName + "_" + Path.GetFileNameWithoutExtension(project.TestClassSourceFile));
                    }
                }
            }
            */

            HashSet<TestProject> ilProjects = new HashSet<TestProject>(_projects.Where(prj => prj.IsILProject));

            foreach (Dictionary<DebugOptimize, List<TestProject>> debugOptProjectMap in _namespaceNameMap.Values.Where(
                dict => dict.Values.Any(l => l.Count > 1 || l.Any(p => _commonClassNames.Contains(p.TestClassName)))
                ))
            {
                if (debugOptProjectMap.Values.Any(prjList => prjList.Any(prj => prj.CompileFiles.Any(f => Path.GetFileNameWithoutExtension(f) == "accum"))))
                {
                    Console.WriteLine("accum!");
                }

                bool haveCsAndIlVersion =
                    debugOptProjectMap.Values.Any(l => l.Any(prj => prj.IsILProject)) &&
                    debugOptProjectMap.Values.Any(l => l.Any(prj => !prj.IsILProject));

                bool existsInMultipleFolders = debugOptProjectMap.Values.Any(pl => pl.Any(
                    p => p.CompileFiles.Any(
                        cf => compileFileToFolderNameMap.TryGetValue(Path.GetFileName(cf), out HashSet<string>? fl) && fl.Count > 1)));

                foreach (TestProject project in debugOptProjectMap.Values.SelectMany(v => v))
                {
                    if (project.RelativePath.Contains("hfa_params"))
                    {
                        Console.WriteLine("Project: {0}", project.AbsolutePath);
                    }

                    if (Path.GetFileName(project.TestClassSourceFile) == "hfa_params.cs")
                    {
                        Console.WriteLine("Project: {0}", project.AbsolutePath);
                    }

                    string deduplicatedName = project.TestClassNamespace;
                    if (deduplicatedName == "")
                    {
                        deduplicatedName = "Test";
                    }
                    deduplicatedName += "_" + Path.GetFileNameWithoutExtension(project.TestClassSourceFile);
                    if (existsInMultipleFolders)
                    {
                        deduplicatedName += "_" + Path.GetFileName(Path.GetDirectoryName(project.TestClassSourceFile)!);
                    }
                    if (haveCsAndIlVersion)
                    {
                        deduplicatedName += (project.IsILProject ? "_il" : "_cs");
                    }
                    project.DeduplicatedNamespaceName = TestProject.SanitizeIdentifier(deduplicatedName);
                }
            }
        }
    }
}