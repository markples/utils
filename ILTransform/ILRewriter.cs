// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Security.Authentication.ExtendedProtection;
using System.Text;
using System.Text.RegularExpressions;

namespace ILTransform
{
    public class ILRewriter
    {
        private static string[] s_xUnitLines =
        {
            ".assembly extern xunit.core {}",
        };

        private static string[] s_factLines =
        {
            ".custom instance void [xunit.core]Xunit.FactAttribute::.ctor() = (",
            "    01 00 00 00",
            ")",
        };

        private static string[] s_csFactLines =
        {
            "[Fact]",
        };

        private static string[] s_processIsolationLines =
        {
            "<RequiresProcessIsolation>true</RequiresProcessIsolation>",
        };

        // Add 'linesToAdd' to 'lines' at index 'index' with indentation copied from 'modelLine'
        // Returns index of lines after the inserted lines
        private static int InsertIndentedLines(List<string> lines, int index, IEnumerable<string> linesToAdd, string modelLine)
        {
            int indent = TestProject.GetIndent(modelLine);
            string indentString = modelLine.Substring(0, indent);
            int oldCount = lines.Count;
            lines.InsertRange(index, linesToAdd.Select(line => indentString + line));
            return index + lines.Count - oldCount;
        }

        private readonly TestProject _testProject;
        private readonly Settings _settings;
        private readonly HashSet<string> _rewrittenFiles;

        public ILRewriter(
            TestProject testProject,
            Settings settings,
            HashSet<string> rewrittenFiles)
        {
            _testProject = testProject;
            _settings = settings;
            _rewrittenFiles = rewrittenFiles;
        }

        // Side effects: Rewrites CS/IL source file and proj file
        public void Rewrite()
        {
            if (!string.IsNullOrEmpty(_testProject.MainClassSourceFile) && _rewrittenFiles.Add(_testProject.MainClassSourceFile))
            {
                RewriteFile(_testProject.MainClassSourceFile);
            }

            RewriteProject(_testProject.NewAbsolutePath ?? _testProject.AbsolutePath);
        }

        // Side effect: Rewrites CS/IL source file (depends on _settings):
        // - Add Fact attribute
        // - Make classes/methods public
        // - Put Main in a class
        // - Remove assembly reference to xunit.core
        // - Add assembly reference to xunit
        // - Rename main namespace/class to deduplicate them across tests
        // - Remove .module
        // - Rename .assembly
        private void RewriteFile(string source)
        {
            List<string> lines = new List<string>(File.ReadAllLines(source));
            bool isILTest = Path.GetExtension(source).ToLower() == ".il";
            bool rewritten = false;

            if (Path.GetFileName(source).Equals("instance.il", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("RewriteFile: {0}", source);
            }

            if (!string.IsNullOrEmpty(_testProject.MainMethodName))
            {
                int lineIndex = _testProject.LastMainMethodDefLine;
                if (lineIndex >= 0)
                {
                    if (_settings.CollapseMainSign
                        && (_testProject.FirstMainMethodDefLine != -1)
                        && (_testProject.FirstMainMethodDefLine < _testProject.LastMainMethodDefLine))
                    {
                        int mainLine = _testProject.FirstMainMethodDefLine;
                        string collapsedLine = lines[mainLine].TrimEnd();
                        for(mainLine++; mainLine <= _testProject.LastMainMethodDefLine; mainLine++)
                        {
                            collapsedLine += " " + lines[mainLine].Trim();
                        }
                        // overwrite first main line and remove the rest
                        lines[_testProject.FirstMainMethodDefLine] = collapsedLine;
                        int mainSignLineLength = _testProject.LastMainMethodDefLine - _testProject.FirstMainMethodDefLine;
                        lines.RemoveRange(_testProject.FirstMainMethodDefLine+1, mainSignLineLength);
                        rewritten = true;
                        // nothing else should be done with this file as indexes are now broken
                    }

                    if (_settings.AddILFactAttributes)
                    {
                        int lineInBody = lines.FindIndex(lineIndex, line => line.Contains('{') || (!isILTest && line.Contains("=>")));
                        if (lineInBody == -1)
                        {
                            Console.Error.WriteLine("Opening brace or => for main method not found in file: {0}", source);
                        }

                        if ((lineInBody >= 0) && !_testProject.HasFactAttribute)
                        {
                            int indentLine = (isILTest ? lineInBody + 1 : lineIndex);
                            string firstMainBodyLine = lines[indentLine];
                            int indent = TestProject.GetIndent(firstMainBodyLine);
                            string indentString = firstMainBodyLine.Substring(0, indent);
                            if (isILTest)
                            {
                                InsertIndentedLines(lines, lineInBody + 1, s_factLines, firstMainBodyLine);
                            }
                            else
                            {
                                lines[_testProject.MainTokenMethodLine] =
                                    ReplaceIdent(lines[_testProject.MainTokenMethodLine], "Main", "TestEntryPoint");
                                lineIndex = InsertIndentedLines(lines, lineIndex, s_csFactLines, firstMainBodyLine);
                            }
                            _testProject.AddedFactAttribute = true;
                            rewritten = true;
                        }
                    }

                    if (_settings.MakePublic && (_testProject.FirstMainMethodDefLine != -1))
                    {
                        if (isILTest)
                        {
                            string line = lines[_testProject.FirstMainMethodDefLine];
                            if (!line.Contains(".method"))
                            {
                                Console.WriteLine("Internal error re-finding .method in {0}", source);
                            }
                            if (TestProject.MakePublic(isILTest: isILTest, ref line, force: true))
                            {
                                lines[_testProject.FirstMainMethodDefLine] = line;
                                rewritten = true;
                            }
                        }
                        else
                        {
                            string line = lines[_testProject.FirstMainMethodDefLine];
                            TestProject.MakePublic(isILTest: isILTest, ref line, force: true);
                            lines[_testProject.FirstMainMethodDefLine] = line;
                            rewritten = true;
                        }
                    }

                    if (_settings.MakePublic)
                    {
                        foreach (string baseClassName in _testProject.MainClassBases)
                        {
                            for (int index = 0; index < lines.Count; index++)
                            {
                                string line = lines[index];
                                if (index != _testProject.MainClassLine &&
                                    (line.Contains("class") || line.Contains("struct")) &&
                                    line.Contains(baseClassName))
                                {
                                    if (TestProject.MakePublic(isILTest: isILTest, ref line, force: true))
                                    {
                                        lines[index] = line;
                                        rewritten = true;
                                    }
                                    break;
                                }
                            }
                        }
                    }

                    /*
                    int closingParen = line.IndexOf(')', mainPos + MainTag.Length);
                    if (!_settings.DeduplicateClassNames)
                    {
                        string replacement = " Test(";
                        lines[lineIndex] = line.Substring(0, mainPos) + replacement + line.Substring(closingParen);
                        rewritten = true;
                    }
                    lines[lineIndex] = line.Substring(0, mainPos) + replacement + line.Substring(mainPos + MainTag.Length);
                    rewritten = true;
                    */

                    /*
                    for (int privateIndex = lineIndex; privateIndex >= lineIndex - 1 && privateIndex >= 0; privateIndex--)
                    {
                        line = lines[privateIndex];
                        int privatePos = line.IndexOf("private ");
                        if (privatePos >= 0)
                        {
                            if (!_settings.DeduplicateClassNames)
                            {
                                line = line.Substring(0, privatePos) + "public" + line.Substring(privatePos + 7);
                                lines[privateIndex] = line;
                                rewritten = true;
                            }
                            break;
                        }
                        int publicPos = line.IndexOf("public ");
                        if (publicPos >= 0)
                        {
                            break;
                        }
                    }
                    */
                }
            }

            if (_settings.MakePublic)
            {
                if (_testProject.MainClassLine >= 0)
                {
                    string line = lines[_testProject.MainClassLine];
                    TestProject.MakePublic(isILTest: isILTest, ref line, force: true);
                    lines[_testProject.MainClassLine] = line;
                    rewritten = true;
                }
                else
                {
                    if (isILTest)
                    {
                        string classLine = $".class public auto ansi Test_{Path.GetFileNameWithoutExtension(source)} extends [mscorlib] System.Object {{";
                        lines.Insert(_testProject.FirstMainMethodDefLine, classLine);

                        // There's a significant problem with inserted lines throwing off the precomputed lines.
                        // The + 1 is for the line we just inserted, but this is a hack.
                        int lastMainMethodBodyLine = _testProject.LastMainMethodBodyLine + 1;
                        int lastMainMethodBodyColumn = _testProject.LastMainMethodBodyColumn;
                        string line = lines[lastMainMethodBodyLine];
                        if (lastMainMethodBodyColumn == line.Length)
                        {
                            lines.Insert(lastMainMethodBodyLine + 1, "}");
                        }
                        else
                        {
                            lines[lastMainMethodBodyLine] =
                                string.Concat(line.AsSpan(0, lastMainMethodBodyColumn), "}", line.AsSpan(lastMainMethodBodyColumn));
                        }
                        rewritten = true;
                    }
                }
            }

            if (_settings.AddILFactAttributes)
            {
                bool hasXunitReference = false;
                string testName = _testProject.TestProjectAlias!;
                bool addFactAttribute = !_testProject.HasFactAttribute && isILTest;
                if (isILTest)
                {
                    for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                    {
                        string line = lines[lineIndex];
                        if (line.StartsWith(".assembly extern xunit.core"))
                        {
                            hasXunitReference = true;
                            if (!line.Contains('}'))
                            {
                                int endLine = lineIndex;
                                do
                                {
                                    endLine++;
                                }
                                while (!lines[endLine].Contains('}'));
                                lines.RemoveRange(lineIndex + 1, endLine - lineIndex);
                                lines[lineIndex] = s_xUnitLines[0];
                                rewritten = true;
                            }
                            break;
                        }
                    }

                    for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                    {
                        string line = lines[lineIndex];
                        if (line.StartsWith(".assembly"))
                        {
                            while (!lines[lineIndex].Contains('}'))
                            {
                                lineIndex++;
                            }

                            line = lines[++lineIndex];
                            if (addFactAttribute && !hasXunitReference)
                            {
                                lines.InsertRange(lineIndex, s_xUnitLines);
                                rewritten = true;
                            }
                            break;
                        }

                        /*
                        int start = assemblyIndex + AssemblyTag.Length;
                        for (; ;)
                        {
                            int start = assemblyIndex + AssemblyTag.Length;
                            for (; ; )
                            {
                                start = line.SkipWhiteSpace(start);
                                const string LibraryTag = "library";
                                if (start + LibraryTag.Length <= line.Length && line.Substring(start, LibraryTag.Length) == LibraryTag)
                                {
                                    start += LibraryTag.Length;
                                    continue;
                                }
                                const string LegacyTag = "legacy";
                                if (start + LegacyTag.Length <= line.Length && line.Substring(start, LegacyTag.Length) == LegacyTag)
                                {
                                    start += LegacyTag.Length;
                                    continue;
                                }

                                if (start + 2 <= line.Length && line[start] == '/' && line[start + 1] == '*')
                                {
                                    start += 2;
                                    while (start + 2 <= line.Length && !(line[start] == '*' && line[start + 1] == '/'))
                                    {
                                        start++;
                                    }
                                    continue;
                                }
                                break;
                            }
                            bool quoted = (start < line.Length && line[start] == '\'');
                            if (quoted)
                            {
                                start++;
                            }
                            int end = start;
                            while (end < line.Length && line[end] != '\'' && (quoted || TestProject.IsIdentifier(line[end])))
                            {
                                end++;
                            }
                            string ident = line.Substring(start, end - start);
                            if (ident != testName)
                            {
                                line = line.Substring(0, start) + (quoted ? "" : "'") + testName + (quoted ? "" : "'") + line.Substring(end);
                                lines[lineIndex] = line;
                                rewritten = true;
                                break;
                            }
                        }
                        */
                    }
                }
            }

            if (_settings.DeduplicateClassNames
                && _testProject.DeduplicatedNamespaceName != null)
            {
                if (_testProject.MainClassNamespace == "")
                {
                    int lineIndex = _testProject.NamespaceLine;
                    lines.Insert(lineIndex, (isILTest ? "." : "") + "namespace " + _testProject.DeduplicatedNamespaceName);
                    lines.Insert(lineIndex + 1, "{");
                    lines.Add("}");
                    for (int i = lineIndex; i < lines.Count; i++)
                    {
                        if (TestProject.TryGetILTypeName(source, lines, i, out string? className))
                        {
                            string qualifiedClassName = _testProject.DeduplicatedNamespaceName + "." + className!;
                            for (int s = lineIndex; s < lines.Count; s++)
                            {
                                if (s != i)
                                {
                                    lines[s] = ReplaceIdent(lines[s], className!, qualifiedClassName, IdentKind.TypeUse);
                                }
                            }
                        }
                    }
                }
                else
                {
                    if (isILTest)
                    {
                        for (int lineIndex = _testProject.NamespaceLine; lineIndex < lines.Count; lineIndex++)
                        {
                            lines[lineIndex] = ReplaceIdent(
                                lines[lineIndex],
                                _testProject.MainClassNamespace,
                                _testProject.DeduplicatedNamespaceName,
                                IdentKind.Namespace);
                        }
                    }
                    else
                    {
                        // C#
                        if (_testProject.NamespaceIdentLine != -1)
                        {
                            lines[_testProject.NamespaceIdentLine] =
                                lines[_testProject.NamespaceIdentLine]
                                .Replace(_testProject.MainClassNamespace, _testProject.DeduplicatedNamespaceName);
                        }
                    }
                }

                rewritten = true;
            }

            bool usingXUnit = (_testProject.LastUsingLine >= 0 && lines[_testProject.LastUsingLine].Contains("Xunit"));
            if (_settings.AddILFactAttributes && !isILTest && !usingXUnit)
            {
                int rewriteLine = _testProject.LastUsingLine;
                if (rewriteLine == -1)
                {
                    rewriteLine = _testProject.LastHeaderCommentLine;
                }
                rewriteLine++;
                lines.Insert(rewriteLine++, "using Xunit;");
                rewritten = true;
            }

            if (_settings.CleanupILModule && isILTest)
            {
                if (lines.RemoveAll(line => line.Contains(".module")) > 0)
                {
                    rewritten = true;
                }
            }

            if (_settings.CleanupILSystemRuntimeReference && isILTest)
            {
                for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                {
                    int assemblyStartLineIndex = lineIndex;
                    string line = lines[lineIndex];
                    int assemblyIndex = line.EndIndexOf(".assembly");
                    if (assemblyIndex < 0)
                    {
                        continue;
                    }

                    int externIndex = line.SkipWhiteSpace(assemblyIndex);
                    // This would miss
                    //
                    // .assembly extern
                    // foo
                    const string externString = "extern ";
                    if (!line.AsSpan(externIndex).StartsWith(externString))
                    {
                        continue;
                    }

                    int nameIndex = line.SkipWhiteSpace(externIndex + externString.Length);
                    const string systemRuntimeString = "System.Runtime";
                    if (!line.AsSpan(nameIndex).StartsWith(systemRuntimeString))
                    {
                        continue;
                    }
                    int afterNameIndex = nameIndex + systemRuntimeString.Length;
                    int nextIndex = line.SkipWhiteSpace(afterNameIndex);
                    if (afterNameIndex != line.Length && afterNameIndex == nextIndex)
                    {
                        // Allows  ".assembly extern System.Runtime<eol>"
                        // Rejects ".assembly extern System.Runtime.Foo"
                        continue;
                    }

                    int braceIndex = nextIndex;
                    while (true)
                    {
                        braceIndex = line.IndexOf('{', braceIndex);
                        if (braceIndex >= 0)
                        {
                            break;
                        }
                        line = lines[++lineIndex];
                        braceIndex = 0;
                    }

                    (int closeBraceLine, int afterCloseBraceColumn) = TestProjectStore.FindCloseBrace(lines, lineIndex, braceIndex + 1);

                    if (afterCloseBraceColumn != lines[closeBraceLine].Length)
                    {
                        Console.WriteLine("Extra characters after } in {0}:{1}", source, closeBraceLine);
                        Console.WriteLine(lines[closeBraceLine]);
                        continue;
                    }

                    lines.RemoveRange(assemblyStartLineIndex, closeBraceLine - assemblyStartLineIndex);
                    lines[assemblyStartLineIndex] = ".assembly extern System.Runtime { .publickeytoken = (B0 3F 5F 7F 11 D5 0A 3A ) }";
                    rewritten = true;
                }
            }

            if (_settings.CleanupILAssembly && isILTest)
            {
                for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                {
                    string line = lines[lineIndex];
                    int assemblyIndex = line.EndIndexOf(".assembly");
                    if (assemblyIndex >= 0)
                    {
                        for (int charIndex = assemblyIndex; charIndex < line.Length; charIndex++)
                        {
                            if (char.IsWhiteSpace(line[charIndex]))
                            {
                                continue;
                            }
                            if (line[charIndex] == '/' && charIndex + 1 < line.Length && line[charIndex + 1] == '*')
                            {
                                charIndex += 2;
                                while (charIndex + 1 < line.Length && !(line[charIndex] == '*' && line[charIndex + 1] == '/'))
                                {
                                    charIndex++;
                                }
                                charIndex++;
                                continue;
                            }
                            int identStart = charIndex;
                            string assemblyName;
                            if (line[charIndex] == '\'')
                            {
                                charIndex++;
                                while (charIndex < line.Length && line[charIndex++] != '\'')
                                {
                                }
                                assemblyName = line.Substring(identStart + 1, charIndex - identStart - 2);
                            }
                            else
                            {
                                while (charIndex < line.Length && TestProject.IsIdentifier(line[charIndex]))
                                {
                                    charIndex++;
                                }
                                assemblyName = line.Substring(identStart, charIndex - identStart);

                                if (assemblyName == "extern")
                                {
                                    break;
                                }
                                if (assemblyName == "legacy" || assemblyName == "library")
                                {
                                    continue;
                                }
                            }
                            int identEnd = charIndex;
                            string sourceName = Path.GetFileNameWithoutExtension(source);
                            if (sourceName != assemblyName)
                            {
                                string end = line.Substring(identEnd);
                                // Check if line was '.assembly foo // as "foo"' and discard it
                                Match asNameMatch = Regex.Match(end, $@"\s*//\s*as\s+(""?){assemblyName}\1\s*$");
                                if (asNameMatch.Success)
                                {
                                    end = end.Substring(0, asNameMatch.Index);
                                }

                                line = line.Substring(0, identStart) + '\'' + sourceName + '\'' + end;
                                lines[lineIndex] = line;
                                rewritten = true;
                            }
                            break;
                        }
                    }
                }
            }

            if (rewritten)
            {
                Utils.WriteAllLines(source, lines, Utils.NewLineAtEndSetting.Preserve);
            }
        }

        // Side effect: rewrites project file
        // - Add RequiresProcessIsolation
        // - Remove OutputType Exe
        // - Updates Compile Include=
        private void RewriteProject(string path)
        {
            List<string> lines = new List<string>(File.ReadAllLines(path));
            bool rewritten = false;
            bool hasRequiresProcessIsolation = _testProject.HasRequiresProcessIsolation;
            string quotedOldSourceName = '"' + Path.GetFileName(_testProject.SourceInfo.MainClassSourceFile) + '"';
            for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                string line = lines[lineIndex];

                // Add RequiresProcessIsolation to first PropertyGroup.
                // Do this before the OutputType removal, which might remove the first PropertyGroup.
                if (_settings.AddProcessIsolation && _testProject.NeedsRequiresProcessIsolation && !hasRequiresProcessIsolation)
                {
                    if (line.Contains("<PropertyGroup"))
                    {
                        int indent = TestProject.GetIndent(line);
                        int nextIndent = TestProject.GetIndent(lines[lineIndex + 1]);
                        // If there's a line indented with the PropertyGroup, use that.  Otherwise just add 2 spaces.
                        string modelLine = (nextIndent > indent) ? lines[lineIndex + 1] : "  " + line;

                        string commentLine = $"<!-- Needed for {string.Join(", ", _testProject.RequiresProcessIsolationReasons)} -->";
                        List<string> insertLines = new List<string>() { commentLine };
                        insertLines.AddRange(s_processIsolationLines);
                        // "+ 1" to add after the line.  Then "- 1" to reset to the last inserted line
                        // so that the loop's lineIndex++ puts us after the insertion.
                        lineIndex = InsertIndentedLines(lines, lineIndex + 1, insertLines, modelLine) - 1;

                        hasRequiresProcessIsolation = true;
                        rewritten = true;
                        continue;
                    }
                }

                const string outputTypeTag = "<OutputType>Exe</OutputType>";
                bool containsOutputType = line.Contains(outputTypeTag);
                if (_settings.AddILFactAttributes && containsOutputType)
                {
                    lines.RemoveAt(lineIndex--);
                    if ((lines[lineIndex].Trim() == "<PropertyGroup>")
                        && (lines[lineIndex + 1].Trim() == "</PropertyGroup>"))
                    {
                        lines.RemoveAt(lineIndex);
                        lines.RemoveAt(lineIndex--);
                    }
                    rewritten = true;
                    continue;
                }

                const string compileTag = "<Compile Include";
                bool containsCompileTag = line.Contains(compileTag);
                const string quotedMSBuildProjectName = "\"$(MSBuildProjectName).il\"";
                if (_testProject.IsILProject && containsCompileTag && (_testProject.NewTestClassSourceFile != null))
                {
                    bool ilMatchesProj =
                        Path.GetFileNameWithoutExtension(_testProject.NewTestClassSourceFile)
                        == Path.GetFileNameWithoutExtension(_testProject.NewAbsolutePath ?? _testProject.AbsolutePath);
                    string replaced = ilMatchesProj
                        ? line.Replace(quotedOldSourceName, quotedMSBuildProjectName)
                        : line.Replace(quotedOldSourceName, '"' + Path.GetFileName(_testProject.NewTestClassSourceFile) + '"');
                    if (replaced == line)
                    {
                        if (!line.Contains(quotedMSBuildProjectName) || !ilMatchesProj)
                        {
                            Console.WriteLine($"Unrecognized <Compile Include=...> in {_testProject.AbsolutePath}");
                        }
                    }
                    else
                    {
                        lines[lineIndex] = replaced;
                        rewritten = true;
                    }
                }

                /*
                const string testKindTag = "<CLRTestKind>BuildAndRun</CLRTestKind>";
                int testKindIndex = line.IndexOf(testKindTag);
                if (testKindIndex >= 0)
                {
                    lines[lineIndex] = line.Substring(0, testKindIndex) + "<CLRTestKind>BuildOnly</CLRTestKind>";
                    rewritten = true;
                    continue;
                }
                */
            }

            if (rewritten)
            {
                Utils.WriteAllLines(path, lines, Utils.NewLineAtEndSetting.Preserve);
            }
        }

        private enum IdentKind
        {
            Namespace,
            TypeUse,
            Other
        }

        private enum TokenKind
        {
            _Illegal,
            WhiteSpace,
            Comment,
            DoubleQuoted,
            SingleQuoted,
            Identifier,
            Other
        }

        private bool IsNamespaceDeclName(List<string> tokens, List<TokenKind> kinds, int index)
            => (index == 3)
            && kinds[0] == TokenKind.Other && tokens[0] == "."
            && (kinds[1] == TokenKind.Identifier || kinds[1] == TokenKind.SingleQuoted) && tokens[1] == "namespace"
            && kinds[2] == TokenKind.WhiteSpace;

        private bool IsNamespacePrefix(List<string> tokens, List<TokenKind> kinds, int index)
            => (index + 2 < tokens.Count)
            && kinds[index + 1] == TokenKind.Other && tokens[index + 1] == "."
            && (kinds[index + 2] == TokenKind.Identifier || kinds[index + 2] == TokenKind.SingleQuoted);

        private bool IsTypePrefix(List<string> tokens, List<TokenKind> kinds, int index)
            => (index + 2 < tokens.Count)
            && kinds[index + 1] == TokenKind.Other
            && ((tokens[index + 1] == "::") || (tokens[index + 1] == "/")) // type::field or type::nestedtype
            && (kinds[index + 2] == TokenKind.Identifier || kinds[index + 2] == TokenKind.SingleQuoted);

        private static string[] TypeDefTokens = { "public", "auto", "ansi" };
        private bool IsTypeNameDef(List<string> tokens, List<TokenKind> kinds, int index)
        {
            for (; index >= 2; index -= 2)
            {
                if (kinds[index - 1] != TokenKind.WhiteSpace) break;
                if (kinds[index - 2] != TokenKind.Identifier && kinds[index - 2] != TokenKind.SingleQuoted) break;
                if (tokens[index - 2] == ".class") return true;
                if (!TypeDefTokens.Contains(tokens[index - 2])) break;
            }
            return false;
        }

        private bool IsTypeNameUse(List<string> tokens, List<TokenKind> kinds, int index)
            => (index >= 2)
            && (kinds[index - 2] == TokenKind.Identifier || kinds[index - 2] == TokenKind.SingleQuoted)
            && (tokens[index - 2] == "class" || tokens[index - 2] == "valuetype")
            && kinds[index - 1] == TokenKind.WhiteSpace;

        private bool IsMethodName(List<string> tokens, List<TokenKind> kinds, int index)
            => (index + 1 < tokens.Count)
            && kinds[index + 1] == TokenKind.Other && tokens[index + 1] == "(";

        private static string[] TypeOperators = { "ldtoken", "box", "initobj", "ldobj", "stobj", "cpobj", "isinst", "castclass", "catch", "sizeof", "ldelema", "newarr" };
        private bool IsOperatorType(List<string> tokens, List<TokenKind> kinds, int index)
            => !IsNamespacePrefix(tokens, kinds, index)
            && (index >= 2)
            && (kinds[index - 2] == TokenKind.Identifier || kinds[index - 2] == TokenKind.SingleQuoted)
            && TypeOperators.Contains(tokens[index - 2])
            && kinds[index - 1] == TokenKind.WhiteSpace;

        private bool IsInheritanceType(List<string> tokens, List<TokenKind> kinds, int index)
        {
            while (--index >= 0)
            {
                if ((kinds[index] == TokenKind.Identifier)
                    && ((tokens[index] == "extends") || (tokens[index] == "implements")))
                {
                    return true;
                }

                if (kinds[index] == TokenKind.Identifier
                    || kinds[index] == TokenKind.WhiteSpace
                    || (kinds[index] == TokenKind.Other && tokens[index] == ","))
                    continue;

                break;
            }

            return false;
        }

        private bool IsVariableName(List<string> tokens, List<TokenKind> kinds, int index)
            => (index >= 2)
            && kinds[index - 2] == TokenKind.Identifier && tokens[index - 2] == "int"
            && kinds[index - 1] == TokenKind.WhiteSpace;

        private List<(string, TokenKind)> SpecialTokens = new List<(string, TokenKind)>()
        {
            (".class", TokenKind.Identifier),
            (".ctor", TokenKind.Identifier),
            ("::", TokenKind.Other),
            ("(", TokenKind.Other),
            (")", TokenKind.Other),
        };

        private string ReplaceIdent(string source, string searchIdent, string replaceIdent, IdentKind searchKind = IdentKind.Other)
        {
            if (!source.Contains(searchIdent))
            {
                return source;
            }

            var tokens = new List<string>();
            var kinds = new List<TokenKind>();

            for (int index = 0, next; index < source.Length; index = next)
            {
                next = index;
                TokenKind kind = TokenKind._Illegal;

                char c = source[next];
                if (c == '\"')
                {
                    next = source.IndexOf('"', next + 1);
                    // next is " or end of line
                    next = (next == -1) ? source.Length : next + 1;
                    kind = TokenKind.DoubleQuoted;
                }
                else if (c == '\'')
                {
                    next = source.IndexOf('\'', next + 1);
                    // next is " or end of line
                    next = (next == -1) ? source.Length : next + 1;
                    kind = TokenKind.SingleQuoted;
                }
                else if (c == '/' && next + 1 < source.Length && source[next + 1] == '/')
                {
                    // Comment - copy over rest of line
                    next = source.Length;
                    kind = TokenKind.Comment;
                }
                else if (char.IsWhiteSpace(c))
                {
                    next = source.SkipWhiteSpace(next + 1);
                    kind = TokenKind.WhiteSpace;
                }
                else
                {
                    var special = SpecialTokens.FirstOrDefault(
                        candidate => next + candidate.Item1.Length <= source.Length
                        && MemoryExtensions.Equals(source.AsSpan(next, candidate.Item1.Length), candidate.Item1.AsSpan(), StringComparison.Ordinal));
                    if (special.Item1 != null)
                    {
                        next += special.Item1.Length;
                        kind = special.Item2;
                    }
                }

                if (next == index)
                {
                    if (!TestProject.IsIdentifier(c))
                    {
                        while (++next < source.Length
                            && !TestProject.IsIdentifier(source[next])
                            && (source[next] != '\'')
                            && !char.IsWhiteSpace(source[next]))
                        {
                            // nothing
                        }
                        kind = TokenKind.Other;
                    }
                    else
                    {
                        while (++next < source.Length && TestProject.IsIdentifier(source[next]))
                        {
                            // nothing
                        }
                        kind = TokenKind.Identifier;
                    }
                }

                tokens.Add(source.Substring(index, next - index));
                kinds.Add(kind);
            }

            var builder = new StringBuilder();
            for (int i = 0; i < tokens.Count; ++i)
            {
                string token = tokens[i];
                if (((kinds[i] == TokenKind.Identifier)
                    && (token == searchIdent))
                    || ((kinds[i] == TokenKind.SingleQuoted)
                        && MemoryExtensions.Equals(token.AsSpan(1, token.Length - 2), searchIdent.AsSpan(), StringComparison.Ordinal)))
                {
                    bool replace = true;
                    if (searchKind == IdentKind.Namespace)
                    {
                        if (IsNamespaceDeclName(tokens, kinds, i)
                            || IsNamespacePrefix(tokens, kinds, i))
                        {
                            // good
                        }
                        else if (IsTypeNameDef(tokens, kinds, i))
                        {
                            replace = false;
                        }
                        else
                        {
                            Console.WriteLine("{0}: Checking for namespace: couldn't determine token kind of token #{1}={2} in",
                                _testProject.AbsolutePath, i, token);
                            Console.WriteLine(source);
                            replace = false;
                        }
                    }
                    else if (searchKind == IdentKind.TypeUse)
                    {
                        if (IsTypePrefix(tokens, kinds, i)
                            || IsTypeNameUse(tokens, kinds, i)
                            || IsInheritanceType(tokens, kinds, i)
                            || IsOperatorType(tokens, kinds, i))
                        {
                            // good
                        }
                        else if (IsNamespaceDeclName(tokens, kinds, i)
                            || IsNamespacePrefix(tokens, kinds, i)
                            || IsMethodName(tokens, kinds, i)
                            || IsTypeNameDef(tokens, kinds, i)
                            || IsVariableName(tokens, kinds, i))
                        {
                            replace = false;
                        }
                        else
                        {
                            Console.WriteLine("{0}: Checking for type: couldn't determine token kind of token #{1}={2} in",
                                _testProject.AbsolutePath, i, token);
                            Console.WriteLine(source);
                            replace = false;
                        }
                    }

                    if (replace)
                    {
                        if (kinds[i] == TokenKind.SingleQuoted) builder.Append('\'');
                        builder.Append(replaceIdent);
                        if (kinds[i] == TokenKind.SingleQuoted) builder.Append('\'');
                        continue;
                    }
                }

                builder.Append(token);
            }
            return builder.ToString();
        }
    }
}
