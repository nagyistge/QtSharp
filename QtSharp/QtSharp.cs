﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CppSharp;
using CppSharp.AST;
using CppSharp.AST.Extensions;
using CppSharp.Generators;
using CppSharp.Passes;
using CppAbi = CppSharp.Parser.AST.CppAbi;

namespace QtSharp
{
    public class QtSharp : ILibrary
    {
        public QtSharp(QtInfo qtInfo)
        {
            this.qtInfo = qtInfo;
        }

        public ICollection<KeyValuePair<string, string>> GetVerifiedWrappedModules()
        {
            for (int i = this.wrappedModules.Count - 1; i >= 0; i--)
            {
                var wrappedModule = this.wrappedModules[i];
                if (!File.Exists(wrappedModule.Key) || !File.Exists(wrappedModule.Value))
                {
                    this.wrappedModules.RemoveAt(i);
                }
            }
            return this.wrappedModules;
        }

        public void Preprocess(Driver driver, ASTContext lib)
        {
            foreach (var unit in lib.TranslationUnits.Where(u => u.FilePath != "<invalid>"))
            {
                IgnorePrivateDeclarations(unit);
            }
            lib.SetClassAsValueType("QByteArray");
            lib.SetClassAsValueType("QListData");
            lib.SetClassAsValueType("QListData::Data");
            lib.SetClassAsValueType("QLocale");
            lib.SetClassAsValueType("QModelIndex");
            lib.SetClassAsValueType("QPoint");
            lib.SetClassAsValueType("QPointF");
            lib.SetClassAsValueType("QSize");
            lib.SetClassAsValueType("QSizeF");
            lib.SetClassAsValueType("QRect");
            lib.SetClassAsValueType("QRectF");
            lib.SetClassAsValueType("QGenericArgument");
            lib.SetClassAsValueType("QGenericReturnArgument");
            lib.SetClassAsValueType("QVariant");
            lib.IgnoreClassMethodWithName("QString", "fromStdWString");
            lib.IgnoreClassMethodWithName("QString", "toStdWString");

            // QString is type-mapped to string so we only need two methods for the conversion
            var qString = lib.FindCompleteClass("QString");
            foreach (var @class in qString.Declarations)
            {
                @class.ExplicitlyIgnore();
            }
            foreach (var method in qString.Methods.Where(m => m.OriginalName != "utf16" && m.OriginalName != "fromUtf16"))
            {
                method.ExplicitlyIgnore();
            }

            // HACK: work around https://github.com/mono/CppSharp/issues/657
            var qSignalMapper = lib.FindCompleteClass("QSignalMapper");
            for (int i = qSignalMapper.Methods.Count - 1; i >= 0; i--)
            {
                Class @class;
                var method = qSignalMapper.Methods[i];
                if (method.Parameters.Count > 0)
                {
                    var type = method.Parameters.Last().Type;
                    var finalType = type.GetFinalPointee() ?? type;
                    if (finalType.TryGetClass(out @class) &&
                        @class.TranslationUnit.Module.OutputNamespace == "QtWidgets")
                    {
                        if (method.Name == "mapped")
                        {
                            qSignalMapper.Methods.RemoveAt(i);
                        }
                        else
                        {
                            method.ExplicitlyIgnore();
                        }
                    }
                }
            }
            var qActionEvent = lib.FindCompleteClass("QActionEvent");
            foreach (var method in qActionEvent.Methods)
            {
                if ((method.Name == "QActionEvent" && method.Parameters.Count == 3) ||
                    method.Name == "action" || method.Name == "before")
                {
                    method.ExplicitlyIgnore();
                }
            }
            var qCamera = lib.FindClass("QCamera").FirstOrDefault(c => !c.IsIncomplete &&
                c.TranslationUnit.Module.OutputNamespace == "QtMultimedia");
            var qMediaPlayer = lib.FindCompleteClass("QMediaPlayer");
            foreach (var method in qCamera.Methods.Union(qMediaPlayer.Methods).Where(m => m.Parameters.Any()))
            {
                Class @class;
                var type = method.Parameters.Last().Type;
                var finalType = type.GetFinalPointee() ?? type;
                if (finalType.TryGetClass(out @class) &&
                    @class.TranslationUnit.Module.OutputNamespace == "QtMultimediaWidgets")
                {
                    method.ExplicitlyIgnore();
                }
            }

            // HACK: work around https://github.com/mono/CppSharp/issues/594
            lib.FindCompleteClass("QGraphicsItem").FindEnum("Extension").Access = AccessSpecifier.Public;
            lib.FindCompleteClass("QAbstractSlider").FindEnum("SliderChange").Access = AccessSpecifier.Public;
            lib.FindCompleteClass("QAbstractItemView").FindEnum("CursorAction").Access = AccessSpecifier.Public;
            lib.FindCompleteClass("QAbstractItemView").FindEnum("State").Access = AccessSpecifier.Public;
            lib.FindCompleteClass("QAbstractItemView").FindEnum("DropIndicatorPosition").Access = AccessSpecifier.Public;
            var classesWithTypeEnums = new[]
                                       {
                                           "QGraphicsEllipseItem", "QGraphicsItemGroup", "QGraphicsLineItem",
                                           "QGraphicsPathItem", "QGraphicsPixmapItem", "QGraphicsPolygonItem",
                                           "QGraphicsProxyWidget", "QGraphicsRectItem", "QGraphicsSimpleTextItem",
                                           "QGraphicsTextItem", "QGraphicsWidget", "QGraphicsSvgItem"
                                       };
            foreach (var enumeration in from @class in classesWithTypeEnums
                                        from @enum in lib.FindCompleteClass(@class).Enums
                                        where string.IsNullOrEmpty(@enum.Name)
                                        select @enum)
            {
                enumeration.Name = "TypeEnum";
            }
        }

        private static void IgnorePrivateDeclarations(DeclarationContext unit)
        {
            foreach (var declaration in unit.Declarations)
            {
                IgnorePrivateDeclaration(declaration);
            }
        }

        private static void IgnorePrivateDeclaration(Declaration declaration)
        {
            if (declaration.Name != null &&
                (declaration.Name.StartsWith("Private", System.StringComparison.Ordinal) ||
                 declaration.Name.EndsWith("Private", System.StringComparison.Ordinal)))
            {
                declaration.ExplicitlyIgnore();
            }
            else
            {
                DeclarationContext declarationContext = declaration as DeclarationContext;
                if (declarationContext != null)
                {
                    IgnorePrivateDeclarations(declarationContext);
                }
            }
        }

        public void Postprocess(Driver driver, ASTContext lib)
        {
            new ClearCommentsPass().VisitLibrary(driver.ASTContext);
            var modules = this.qtInfo.LibFiles.Select(l => GetModuleNameFromLibFile(l));
            var s = System.Diagnostics.Stopwatch.StartNew();
            new GetCommentsFromQtDocsPass(this.qtInfo.Docs, modules).VisitLibrary(driver.ASTContext);
            System.Console.WriteLine("Documentation done in: {0}", s.Elapsed);
            new CaseRenamePass(
                RenameTargets.Function | RenameTargets.Method | RenameTargets.Property | RenameTargets.Delegate |
                RenameTargets.Field | RenameTargets.Variable,
                RenameCasePattern.UpperCamelCase).VisitLibrary(driver.ASTContext);

            var qChar = lib.FindCompleteClass("QChar");
            var op = qChar.FindOperator(CXXOperatorKind.ExplicitConversion)
                .FirstOrDefault(o => o.Parameters[0].Type.IsPrimitiveType(PrimitiveType.Char));
            if (op != null)
                op.ExplicitlyIgnore();
            op = qChar.FindOperator(CXXOperatorKind.Conversion)
                .FirstOrDefault(o => o.Parameters[0].Type.IsPrimitiveType(PrimitiveType.Int));
            if (op != null)
                op.ExplicitlyIgnore();
            // QString is type-mapped to string so we only need two methods for the conversion
            // go through the methods a second time to ignore free operators moved to the class
            var qString = lib.FindCompleteClass("QString");
            foreach (var method in qString.Methods.Where(
                m => !m.Ignore && m.OriginalName != "utf16" && m.OriginalName != "fromUtf16"))
            {
                method.ExplicitlyIgnore();
            }

            foreach (var module in driver.Options.Modules)
            {
                var prefix = Platform.IsWindows ? string.Empty : "lib";
                var extension = Platform.IsWindows ? ".dll" : Platform.IsMacOS ? ".dylib" : ".so";
                var inlinesLibraryFile = string.Format("{0}{1}{2}", prefix, module.InlinesLibraryName, extension);
                var inlinesLibraryPath = Path.Combine(driver.Options.OutputDir, Platform.IsWindows ? "release" : string.Empty, inlinesLibraryFile);
                this.wrappedModules.Add(new KeyValuePair<string, string>(module.LibraryName + ".dll", inlinesLibraryPath));
            }
        }

        public void Setup(Driver driver)
        {
            driver.Options.GeneratorKind = GeneratorKind.CSharp;
            driver.Options.MicrosoftMode = false;
            driver.Options.NoBuiltinIncludes = true;
            driver.Options.TargetTriple = this.qtInfo.Target;
            driver.Options.Abi = CppAbi.Itanium;
            driver.Options.Verbose = true;
            driver.Options.GenerateInterfacesForMultipleInheritance = true;
            driver.Options.GeneratePropertiesAdvanced = true;
            driver.Options.UnityBuild = true;
            driver.Options.IgnoreParseWarnings = true;
            driver.Options.CheckSymbols = true;
            driver.Options.GenerateSingleCSharpFile = true;
            driver.Options.GenerateInlines = true;
            driver.Options.CompileCode = true;
            driver.Options.GenerateDefaultValuesForArguments = true;
            driver.Options.GenerateConversionOperators = true;
            driver.Options.MarshalCharAsManagedChar = true;

            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            const string qt = "Qt";
            foreach (var libFile in this.qtInfo.LibFiles)
            {
                string qtModule = GetModuleNameFromLibFile(libFile);
                var module = new CppSharp.AST.Module();
                module.LibraryName = string.Format("{0}Sharp", qtModule);
                module.OutputNamespace = qtModule;
                module.Headers.Add(qtModule);
                var moduleName = qtModule.Substring(qt.Length);
                if (Platform.IsMacOS)
                {
                    var framework = string.Format("{0}.framework", qtModule);
                    module.IncludeDirs.Add(Path.Combine(this.qtInfo.Libs, framework));
                    module.IncludeDirs.Add(Path.Combine(this.qtInfo.Libs, framework, "Headers"));
                    if (moduleName == "UiPlugin")
                    {
                        var qtUiPlugin = string.Format("Qt{0}.framework", moduleName);
                        module.IncludeDirs.Add(Path.Combine(this.qtInfo.Libs, qtUiPlugin));
                        module.IncludeDirs.Add(Path.Combine(this.qtInfo.Libs, qtUiPlugin, "Headers"));
                    }
                }
                else
                {
                    var moduleInclude = Path.Combine(qtInfo.Headers, qtModule);
                    if (Directory.Exists(moduleInclude))
                        module.IncludeDirs.Add(moduleInclude);
                    if (moduleName == "Designer")
                    {
                        module.IncludeDirs.Add(Path.Combine(qtInfo.Headers, "QtUiPlugin"));
                    }
                }
                if (moduleName == "Designer")
                {
                    foreach (var header in Directory.EnumerateFiles(module.IncludeDirs.Last(), "*.h"))
                    {
                        module.Headers.Add(Path.GetFileName(header));
                    }
                }
                module.Libraries.Add(libFile);
                if (moduleName == "Core")
                {
                    module.CodeFiles.Add(Path.Combine(dir, "QObject.cs"));
                    module.CodeFiles.Add(Path.Combine(dir, "QChar.cs"));
                    module.CodeFiles.Add(Path.Combine(dir, "QEvent.cs"));
                    module.CodeFiles.Add(Path.Combine(dir, "_iobuf.cs"));
                }

                driver.Options.Modules.Add(module);
            }

            foreach (var systemIncludeDir in this.qtInfo.SystemIncludeDirs)
                driver.Options.addSystemIncludeDirs(systemIncludeDir);
            
            if (Platform.IsMacOS)
            {
                foreach (var frameworkDir in this.qtInfo.FrameworkDirs)
                    driver.Options.addArguments(string.Format("-F{0}", frameworkDir));
                driver.Options.addArguments(string.Format("-F{0}", qtInfo.Libs));
            }

            driver.Options.addIncludeDirs(qtInfo.Headers);
            
            driver.Options.addLibraryDirs(Platform.IsWindows ? qtInfo.Bins : qtInfo.Libs);
        }

        public static string GetModuleNameFromLibFile(string libFile)
        {
            var qtModule = Path.GetFileNameWithoutExtension(libFile);
            if (Platform.IsWindows)
            {
                return "Qt" + qtModule.Substring("Qt".Length + 1);
            }
            return libFile.Substring("lib".Length);
        }

        public void SetupPasses(Driver driver)
        {
            driver.TranslationUnitPasses.AddPass(new CompileInlinesPass(this.qtInfo.QMake, this.qtInfo.Make));
            driver.TranslationUnitPasses.AddPass(new GenerateSignalEventsPass());
            driver.TranslationUnitPasses.AddPass(new GenerateEventEventsPass());
            driver.TranslationUnitPasses.AddPass(new RemoveQObjectMembersPass());
        }

        private readonly QtInfo qtInfo;
        private List<KeyValuePair<string, string>> wrappedModules = new List<KeyValuePair<string, string>>();
    }
}
