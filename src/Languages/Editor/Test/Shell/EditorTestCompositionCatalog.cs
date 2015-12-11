﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.ComponentModel.Composition.Primitives;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Common.Core;
using Microsoft.Common.Core.Shell;
using Microsoft.Win32;

namespace Microsoft.Languages.Editor.Test.Shell {
    /// <summary>
    /// Composition catalog that is primarily used in interactive tests.
    /// It is assigned to EditorShell.Current.CompositionService.
    /// In interactive tests catalog that also includes host application
    /// objects such as VS components is not suitable as it may be exporting
    /// objects that cannot be instantiated in a limited test environment.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public class EditorTestCompositionCatalog : ICompositionCatalog {
        private static CompositionContainer _container;
        private static object _containerLock = new object();

        private string _idePath;
        private string _editorPath;
        private string _privatePath;
        private string _cpsPath;
        private string _sharedPath;

        private static string _partsData;
        private static string _exportsData;

        private static string[] _rPackageAssemblies = new string[] {
            "Microsoft.Markdown.Editor.dll",
            "Microsoft.Languages.Editor.dll",
            "Microsoft.Languages.Editor.Application.dll",
            "Microsoft.R.Editor.dll",
            "Microsoft.R.Editor.Test.dll",
            "Microsoft.R.Support.dll",
            "Microsoft.R.Support.Test.dll",
            "Microsoft.R.Common.Core.dll",
            "Microsoft.R.Host.Client.dll",
        };

        private static string[] _editorAssemblies = new string[]
        {
            "Microsoft.VisualStudio.CoreUtility.dll",
            "Microsoft.VisualStudio.Editor.dll",
            "Microsoft.VisualStudio.Language.Intellisense.dll",
            "Microsoft.VisualStudio.Platform.VSEditor.dll",
            "Microsoft.VisualStudio.Text.Data.dll",
            "Microsoft.VisualStudio.Text.Logic.dll",
            "Microsoft.VisualStudio.Text.UI.dll",
            "Microsoft.VisualStudio.Text.UI.Wpf.dll",
        };

        private static string[] _cpsAssemblies = new string[]
        {
            "Microsoft.VisualStudio.ProjectSystem.Implementation.dll",
            "Microsoft.VisualStudio.ProjectSystem.VS.Implementation.dll"
        };

        private static string[] _projectAssemblies = new string[]
         {
            "Microsoft.VisualStudio.ProjectSystem.Utilities.v14.0.dll",
            "Microsoft.VisualStudio.ProjectSystem.V14Only.dll",
            "Microsoft.VisualStudio.ProjectSystem.VS.V14Only.dll",
         };

        private static string[] _additionalAssemblies = new string[0];

        public static ICompositionCatalog Current { get; private set; }

        public EditorTestCompositionCatalog(string[] additionalAssemblies) {
            _additionalAssemblies = additionalAssemblies;
            TryCreateContainer();
        }

        private EditorTestCompositionCatalog() {
            Current = this;
            TryCreateContainer();
        }

        private void TryCreateContainer() {
            lock (_containerLock) {
                if (_container == null) {
                    _container = CreateContainer();
                }
            }
        }

        private Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args) {
            string name = args.Name.Substring(0, args.Name.IndexOf(',')) + ".dll";
            Assembly asm = null;

            if (!string.IsNullOrEmpty(_privatePath)) {
                string path = Path.Combine(_privatePath, name);

                if (File.Exists(path)) {
                    asm = Assembly.LoadFrom(path);
                }
            }

            if (asm == null && !string.IsNullOrEmpty(_idePath)) {
                string path = Path.Combine(_idePath, name);

                if (File.Exists(path)) {
                    asm = Assembly.LoadFrom(path);
                }
            }

            if (asm == null && !string.IsNullOrEmpty(_sharedPath)) {
                string path = Path.Combine(_sharedPath, name);

                if (File.Exists(path)) {
                    asm = Assembly.LoadFrom(path);
                }
            }

            return asm;
        }

        private static string GetHostVersion() {
            string version = Environment.GetEnvironmentVariable("ExtensionsVSVersion");

            foreach (string checkVersion in new string[]
            {
                "14.0",
            }) {
                if (string.IsNullOrEmpty(version)) {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\" + checkVersion)) {
                        if (key != null) {
                            version = checkVersion;
                        }
                    }
                }
            }

            return version;
        }

        private static string GetHostExePath() {
            string path = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\VisualStudio\" + GetHostVersion(), "InstallDir", string.Empty) as string;
            return path;
        }

        private CompositionContainer CreateContainer() {
            string thisAssembly = Assembly.GetExecutingAssembly().Location;
            string assemblyLoc = Path.GetDirectoryName(thisAssembly);

            _idePath = GetHostExePath();
            _editorPath = Path.Combine(_idePath, @"CommonExtensions\Microsoft\Editor");
            _privatePath = Path.Combine(_idePath, @"PrivateAssemblies\");
            _cpsPath = Path.Combine(_idePath, @"CommonExtensions\Microsoft\Project");
            _sharedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Common Files\Microsoft Shared\MsEnv\PublicAssemblies");

            AggregateCatalog aggregateCatalog = new AggregateCatalog();
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;

            foreach (string asmName in _editorAssemblies) {
                string asmPath = Path.Combine(_editorPath, asmName);
                Assembly editorAssebmly = Assembly.LoadFrom(asmPath);

                AssemblyCatalog editorCatalog = new AssemblyCatalog(editorAssebmly);
                aggregateCatalog.Catalogs.Add(editorCatalog);
            }

            foreach (string asmName in _cpsAssemblies) {
                string asmPath = Path.Combine(_cpsPath, asmName);
                Assembly editorAssebmly = Assembly.LoadFrom(asmPath);

                AssemblyCatalog editorCatalog = new AssemblyCatalog(editorAssebmly);
                aggregateCatalog.Catalogs.Add(editorCatalog);
            }

            foreach (string asmName in _projectAssemblies) {
                string asmPath = Path.Combine(_privatePath, asmName);
                Assembly editorAssebmly = Assembly.LoadFrom(asmPath);

                AssemblyCatalog editorCatalog = new AssemblyCatalog(editorAssebmly);
                aggregateCatalog.Catalogs.Add(editorCatalog);
            }

            foreach (string assemblyName in _rPackageAssemblies) {
                AddAssemblyToCatalog(assemblyLoc, assemblyName, aggregateCatalog);
            }

            foreach (string assemblyName in _additionalAssemblies) {
                AddAssemblyToCatalog(assemblyLoc, assemblyName, aggregateCatalog);
            }

            AssemblyCatalog thisAssemblyCatalog = new AssemblyCatalog(Assembly.GetExecutingAssembly());
            aggregateCatalog.Catalogs.Add(thisAssemblyCatalog);


            var container = BuildCatalog(aggregateCatalog);
            AppDomain.CurrentDomain.AssemblyResolve -= CurrentDomain_AssemblyResolve;

            return container;
        }

        private static void AddAssemblyToCatalog(string assemblyLoc, string assemblyName, AggregateCatalog aggregateCatalog) {
            string[] paths = new string[]
            {
                Path.Combine(assemblyLoc, assemblyName),
            };

            try {
                Assembly assembly = null;

                foreach (string path in paths) {
                    if (File.Exists(path)) {
                        assembly = Assembly.LoadFrom(path);
                        break;
                    }
                }

                if (assembly == null) {
                    throw new FileNotFoundException(assemblyName);
                }

                AssemblyCatalog editorCatalog = new AssemblyCatalog(assembly);
                aggregateCatalog.Catalogs.Add(editorCatalog);
            } catch (Exception) {
                Debug.Assert(false, "Can't find editor assembly: " + assemblyName);
            }
        }

        private CompositionContainer BuildCatalog(AggregateCatalog aggregateCatalog) {
            CompositionContainer container = new CompositionContainer(aggregateCatalog, isThreadSafe: true);

            StringBuilder parts = new StringBuilder();
            StringBuilder exports = new StringBuilder();
            foreach (object o in container.Catalog.Parts) {

                ComposablePartDefinition part = o as ComposablePartDefinition;
                if (part == null) {
                    parts.AppendLine("PART MISSING: " + o.ToString());
                    exports.AppendLine("PART MISSING: " + o.ToString());
                    continue;
                }

                parts.AppendLine("===============================================================");
                parts.AppendLine(part.ToString());

                exports.AppendLine("===============================================================");
                exports.AppendLine(part.ToString());

                bool first = true;

                if (part.ExportDefinitions.FirstOrDefault() != null) {
                    parts.AppendLine("\t --- EXPORTS --");
                    exports.AppendLine("\t --- EXPORTS --");

                    foreach (ExportDefinition exportDefinition in part.ExportDefinitions) {
                        parts.AppendLine("\t" + exportDefinition.ContractName);
                        exports.AppendLine("\t" + exportDefinition.ContractName);

                        foreach (KeyValuePair<string, object> kvp in exportDefinition.Metadata) {
                            string valueString = kvp.Value != null ? kvp.Value.ToString() : string.Empty;

                            parts.AppendLine("\t" + kvp.Key + " : " + valueString);
                            exports.AppendLine("\t" + kvp.Key + " : " + valueString);
                        }

                        if (first) {
                            first = false;
                        } else {
                            parts.AppendLine("------------------------------------------------------");
                            exports.AppendLine("------------------------------------------------------");
                        }
                    }
                }

                if (part.ImportDefinitions.FirstOrDefault() != null) {
                    parts.AppendLine("\t --- IMPORTS ---");

                    foreach (ImportDefinition importDefinition in part.ImportDefinitions) {
                        parts.AppendLine("\t" + importDefinition.ContractName);
                        parts.AppendLine("\t" + importDefinition.Constraint.ToString());
                        parts.AppendLine("\t" + importDefinition.Cardinality.ToString());

                        if (first) {
                            first = false;
                        } else {
                            parts.AppendLine("------------------------------------------------------");
                        }
                    }
                }
                _partsData = parts.ToString();
                _exportsData = exports.ToString();
            }

            return container;
        }

        #region ICompositionCatalog
        public ICompositionService CompositionService => _container;

        public ExportProvider ExportProvider => _container;

        public CompositionContainer Container => _container;
        #endregion
    }
}
