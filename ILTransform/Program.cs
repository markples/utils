// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.CompilerServices;

namespace ILTransform
{
    public class Settings
    {
        public bool DeduplicateClassNames;
        public string ClassToDeduplicate = "";
        public bool DeduplicateProjectNames;
        public bool FixILFileNames;
        public bool FixImplicitSharedLibraries;
        public bool AddILFactAttributes;
        public bool MakePublic;
        public bool CollapseMainSign; // should be used alone as it breaks most of the indexes
        public bool AddProcessIsolation;
        public bool UnifyDbgRelProjects;
        public bool CleanupILModule;
        public bool CleanupILAssembly;
        public bool CleanupILSystemRuntimeReference;
        public bool GenerateWrappers;
    }

    public class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                List<string> testRoots = new List<string>();
                Settings settings = new Settings();
                foreach (string arg in args)
                {
                    if (arg[0] == '-')
                    {
                        if (arg.StartsWith("-d"))
                        {
                            settings.DeduplicateClassNames = true;
                            int index = 2;
                            while (index < arg.Length && !TestProject.IsIdentifier(arg[index]))
                            {
                                index++;
                            }
                            if (index < arg.Length)
                            {
                                settings.ClassToDeduplicate = arg.Substring(index);
                            }
                        }
                        else if (arg == "-i")
                        {
                            settings.FixImplicitSharedLibraries = true;
                        }
                        else if (arg == "-ilfact")
                        {
                            settings.AddILFactAttributes = true;
                        }
                        else if (arg == "-public")
                        {
                            settings.MakePublic = true;
                        }
                        else if (arg =="-collapse-main-sign")
                        {
                            settings.CollapseMainSign = true;
                        }
                        else if (arg == "-prociso")
                        {
                            settings.AddProcessIsolation = true;
                        }
                        else if (arg == "-p")
                        {
                            settings.UnifyDbgRelProjects = true;
                        }
                        else if (arg == "-m")
                        {
                            settings.CleanupILModule = true;
                        }
                        else if (arg == "-a")
                        {
                            settings.CleanupILAssembly = true;
                        }
                        else if (arg == "-sr")
                        {
                            settings.CleanupILSystemRuntimeReference = true;
                        }
                        else if (arg == "-n")
                        {
                            settings.DeduplicateProjectNames = true;
                        }
                        else if (arg == "-ilfile")
                        {
                            settings.FixILFileNames = true;
                        }
                        else if (arg == "-w")
                        {
                            settings.GenerateWrappers = true;
                        }
                        else
                        {
                            throw new Exception(string.Format("Unsupported option '{0}'", arg));
                        }
                    }
                    else
                    {
                        testRoots.Add(arg);
                    }
                }

                if (settings.UnifyDbgRelProjects
                    && (settings.DeduplicateProjectNames
                        || settings.FixILFileNames
                        || settings.FixImplicitSharedLibraries
                        || settings.AddILFactAttributes
                        || settings.MakePublic
                        || settings.CollapseMainSign
                        || settings.AddProcessIsolation
                        || settings.UnifyDbgRelProjects
                        || settings.CleanupILModule
                        || settings.CleanupILAssembly
                        || settings.CleanupILSystemRuntimeReference
                        || settings.GenerateWrappers))
                {
                    throw new Exception("-p is not compatible with other rewriting options");
                }

                if (testRoots.Count() == 0)
                {
                    throw new Exception("Usage: ILTransform <test source root, e.g. d:\\git\\runtime\\src\\tests> [-d]");
                }

                string wrapperRoot = Path.Combine(testRoots[0], "generated", "wrappers");
                string logPath = Path.Combine(wrapperRoot, "wrapper.log");
                Directory.CreateDirectory(wrapperRoot);
                foreach (string file in Directory.GetFiles(wrapperRoot))
                {
                    File.Delete(file);
                }

                TestProjectStore testStore = new TestProjectStore();
                testStore.ScanTrees(testRoots);
                testStore.GenerateExternAliases();

                if (settings.FixImplicitSharedLibraries)
                {
                    // TODO
                }

                if (settings.UnifyDbgRelProjects)
                {
                    testStore.UnifyDbgRelProjects();
                }
                if (settings.DeduplicateProjectNames)
                {
                    testStore.DeduplicateProjectNames();
                }
                if (settings.FixILFileNames)
                {
                    testStore.FixILFileNames();
                }

                if (!settings.UnifyDbgRelProjects)
                {
                    testStore.RewriteAllTests(settings);
                }
                if (settings.GenerateWrappers)
                {
                    testStore.GenerateAllWrappers(wrapperRoot);
                }

                using (StreamWriter log = new StreamWriter(logPath))
                {
                    testStore.DumpFolderStatistics(log);
                    testStore.DumpDebugOptimizeStatistics(log);
                    testStore.DumpIrregularProjectSuffixes(log);
                    testStore.DumpImplicitSharedLibraries(log);
                    testStore.DumpMultiSourceProjects(log);
                    testStore.DumpMultiProjectSources(log);
                    testStore.DumpDuplicateProjectContent(log);
                    testStore.DumpDuplicateSimpleProjectNames(log);
                    testStore.DumpDuplicateEntrypointClasses(log);
                    testStore.DumpProjectsWithoutFactAttributes(log);
                    testStore.DumpCommandLineVariations(log);
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Fatal error: {0}", ex.ToString());
                return 1;
            }
        }
    }
}
