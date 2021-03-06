/**********************************************************************

  Ruby.NET Runtime Library
  Originally developed at Queensland University of Technology
 
**********************************************************************/

using System.CodeDom;
using Ruby.Runtime;
using Ruby;
using System.Collections.Generic;
using System.Text;
using System.Reflection;
using PERWAPI;
using Microsoft.Build.Utilities;


namespace Ruby.Compiler.AST {

    public class SOURCEFILE : Scope {
        internal ClassDef fileClass;
        static internal CodeGenContext LoadMethod;

        internal SOURCEFILE(YYLTYPE location)
            : base(null, location) {
        }

        internal static bool RemoveAllocatorDefinition(ClassSkeleton classSkel) {
            //PERWAPI.Method ctor = classSkel.perwapiClass.GetMethodDesc(".ctor", new Type[] { Runtime.ClassRef });
            PERWAPI.CILInstruction[] initInstructions = classSkel.initMethod.GetCodeBuffer().GetInstructions();

            for (int i = 0; i < initInstructions.Length; i++) {
                if (initInstructions[i] is PERWAPI.MethInstr) {
                    PERWAPI.MethInstr call = (PERWAPI.MethInstr)(initInstructions[i]);
                    if (call.GetMethod() == Runtime.Class.define_alloc_func) {
                        classSkel.initMethod.GetCodeBuffer().RemoveInstructions(i - 2, i);
                        return true;
                    }
                }
            }

            return false;
        }

        internal static bool RedefineConstructor(PERWAPI.MethodDef ctor, PERWAPI.Class perwapiClass) {
            PERWAPI.CILInstruction[] ctorInstructions = ctor.GetCodeBuffer().GetInstructions();

            // try and find a constructor in the base class
            PERWAPI.Method superClassConstructor = null;

            for (int i = 0; i < ctorInstructions.Length; i++) {
                if (ctorInstructions[i] is PERWAPI.MethInstr) {
                    PERWAPI.MethInstr inst = (PERWAPI.MethInstr)(ctorInstructions[i]);
                    PERWAPI.Method calledCtor = inst.GetMethod();
                    if (calledCtor.Name() == ".ctor") {
                        PERWAPI.Type[] calledCtorParams = calledCtor.GetParTypes();
                        PERWAPI.Method[] superClassConstructors = perwapiClass.GetMethodDescs(".ctor");
                        superClassConstructor = perwapiClass.GetMethodDesc(".ctor", calledCtorParams);
                        int pos = i;

                        if (superClassConstructor == null) {
                            if (calledCtorParams.Length == 1) {
                                string paramTypeName = calledCtorParams[0].TypeName();
                                foreach (PERWAPI.Method superCtor in superClassConstructors) {
                                    PERWAPI.Type[] superCtorParams = superCtor.GetParTypes();
                                    if (superCtorParams.Length == 1) {
                                        string superParamTypeName = superCtorParams[0].TypeName();
                                        if (paramTypeName == "Ruby.Class" && superParamTypeName == paramTypeName)
                                            superClassConstructor = superCtor;
                                    }
                                }
                            }

                            if (superClassConstructor == null) {
                                // fall back on zero-arg constructor
                                superClassConstructor = perwapiClass.GetMethodDesc(".ctor", new Type[0]);

                                if (superClassConstructor == null)
                                    return false;

                                if (calledCtorParams.Length > 0) {
                                    for (int j = 0; j < calledCtorParams.Length; j++) {
                                        ctor.GetCodeBuffer().RemoveInstruction(pos - 1);
                                        pos--;
                                    }
                                }
                            }
                        }

                        ctor.GetCodeBuffer().ReplaceInstruction(pos);
                        ctor.GetCodeBuffer().MethInst(MethodOp.call, superClassConstructor);
                        ctor.GetCodeBuffer().EndInsert();
                        return true;
                    }
                }
            }

            return false;
        }


        internal static void SuperclassPostPass(CodeGenContext context, List<PERWAPI.ReferenceScope> peFiles) {
            foreach (ClassSkeletonPostPass postPass in context.postPassList) {
                PERWAPI.Class perwapiClass = ClassSkeleton.FindPERWAPIClass(postPass.subClass, postPass.superClassNode, peFiles);
                if (perwapiClass != null) {
                    PERWAPI.Class perwapiClassRef;
                    if (perwapiClass is PERWAPI.ClassDef)
                        perwapiClassRef = ((PERWAPI.ClassDef)perwapiClass).MakeRefOf();
                    else
                        perwapiClassRef = (PERWAPI.ClassRef)perwapiClass;

                    postPass.subClassDef.SuperType = perwapiClassRef;
                    // redefine the constructor
                    PERWAPI.MethodDef[] ctors = postPass.subClassDef.GetMethods(".ctor");

                    foreach (PERWAPI.MethodDef ctor in ctors) {
                        if (!RedefineConstructor(ctor, perwapiClass)) {
                            Compiler.InteropWarning("No zero-arg constructor found for " + perwapiClass.Name() + ", no interop class generated for " + postPass.subClassDef.Name());
                            context.Assembly.RemoveClass(postPass.subClassDef);
                            RemoveAllocatorDefinition(postPass.subClass);
                            context.Assembly.RemoveClass(postPass.subClass.allocator);
                        }
                    }
                } else {
                    Compiler.InteropWarning("Superclass not found for " + postPass.subClass.name);
                }
            }
        }

        internal static PERWAPI.PEFile GenerateCode(List<SOURCEFILE> files, List<PERWAPI.ReferenceScope> peFiles, string outfile, List<KeyValuePair<string, object>> options, bool GUI) {
            CodeGenContext context = new CodeGenContext();
            context.peFiles = peFiles;

            System.IO.FileInfo file = new System.IO.FileInfo(outfile);
            string basename = file.Name.Substring(0, file.Name.Length - file.Extension.Length);

            context.CreateAssembly(file.DirectoryName, file.Name, basename, GUI);

            for (int i = 1; i < files.Count; i++)
                files[i].GenerateClassForFile(context, File.stripExtension(files[i].location.file), file.Extension == ".dll", files);

            ClassDef mainClass = files[0].GenerateClassForFile(context, File.stripExtension(files[0].location.file), file.Extension == ".dll", files);
//?            ClassDef mainClass = files[0].GenerateClassForFile(context, File.stripExtension(files[0].location.file), file.Extension == ".dll", files);

            SuperclassPostPass(context, peFiles);

            if (file.Extension == ".exe") {
                MethodDef Options = GenerateOptionsMethod(context, options);
                GenerateMainMethod(context, mainClass, Options, files);
            }

            return context.Assembly;
        }


        internal static void GenerateMainMethod(CodeGenContext context, ClassDef fileClass, MethodDef SetOptions, List<SOURCEFILE> files) {
            // public static void Main(string[] args) {
            CodeGenContext Main = context.CreateModuleMethod("Main", PrimitiveType.Void, new Param[] { new Param(ParamAttr.Default, "args", new PERWAPI.ZeroBasedArray(PrimitiveType.String)) });

            Main.Method.DeclareEntryPoint();

            PERWAPI.CILLabel endLabel = Main.NewLabel();

            // try {
            Main.StartBlock(Clause.Try);

            if (SetOptions != null) {
                //    SetOptions(args);
                Main.ldarg("args");
                Main.call(SetOptions);
            }

            // register other ruby source files in assembly so that they can be loaded if requested
            foreach (SOURCEFILE f in files) {
                // Ruby.Runtime.Program.AddProgram(filename, fileClass);
                Main.ldstr(File.stripExtension(f.location.file));
                Main.ldtoken(f.fileClass);
                Main.call(Runtime.SystemType.GetTypeFromHandle);
                Main.call(Runtime.Program.AddProgram);
            }

            // Explicit load
            // Load(ruby_top_self, null);
            Main.ldsfld(Runtime.Object.ruby_top_self);
            Main.ldnull();
            Main.call(LoadMethod.Method);
            Main.pop();

            Main.Goto(endLabel);

            // }
            TryBlock block = Main.EndTryBlock();

            // finally {
            Main.StartBlock(Clause.Finally);

            //    Program.ruby_stop();
            Main.call(Runtime.Program.ruby_stop);

            Main.endfinally();

            // }
            Main.EndFinallyBlock(block);

            Main.CodeLabel(endLabel);

            Main.ret();
            Main.Close();
            // }
        }

        internal PERWAPI.PEFile GenerateCode(string fullFileName, string dll_or_exe, List<KeyValuePair<string, object>> runtime_options, bool GUI) {
            System.IO.FileInfo file = new System.IO.FileInfo(File.WindowsPath(fullFileName));

            string fileName;
            // BBTAG: try using absolute path
            //fileName = File.fileNameToClassName(fullFileName);

            if (file.Extension == ".rb")
                fileName = file.Name.Substring(0, file.Name.Length - 3);
            else
                fileName = file.Name;

            CodeGenContext context = new CodeGenContext();

            //rifraf            context.CreateAssembly(file.DirectoryName, fileName + dll_or_exe, fileName, GUI);
            context.CreateAssembly(file.DirectoryName, File.stripExtension(fullFileName) + dll_or_exe, fullFileName, GUI);
            //rifraf            ClassDef mainClass = GenerateClassForFile(context, fileName, false, new List<SOURCEFILE>());
            ClassDef mainClass = GenerateClassForFile(context, File.stripExtension(fullFileName), false, new List<SOURCEFILE>());

            SuperclassPostPass(context, new List<PERWAPI.ReferenceScope>());

            if (dll_or_exe == ".exe") {
                MethodDef Options = GenerateOptionsMethod(context, runtime_options);
                GenerateMainMethod(context, mainClass, Options, new List<SOURCEFILE>());
            }

            return context.Assembly;
        }

        private void CreateClassForFile(CodeGenContext context, string file_name) {
            // public class file_name: System.Object {
            // rifraf
            fileClass = context.CreateGlobalClass("_Internal", "SourceFile_" + file_name.Replace(@"\", "_").Replace(".", "_"), Runtime.SystemObjectRef);
        }

        private PERWAPI.ClassDef GenerateClassForFile(CodeGenContext context, string file_name, bool autoLoad, List<SOURCEFILE> files) {
            if (fileClass == null)
                CreateClassForFile(context, file_name);

            // internal static object Load(object recv, Caller caller, Proc block)
            LoadMethod = context.CreateMethod(fileClass, MethAttr.PublicStatic, "Load", PrimitiveType.Object, new Param[] { new Param(ParamAttr.Default, "recv", PrimitiveType.Object), new Param(ParamAttr.Default, "caller", Runtime.FrameRef) });

            LoadMethod.startMethod(location);
            AddScopeLocals(LoadMethod);
            AddScopeBody(LoadMethod);
            LoadMethod.ReleaseLocal(0, true);

            // }
            LoadMethod.Close();

            if (autoLoad) {
                // accessing this field should trigger the .cctor to load the main source file
                CodeGenContext.AddField(fileClass, FieldAttr.PublicStatic, "loaded", PrimitiveType.Boolean);

                // public static .cctor() {
                CodeGenContext cctor = context.CreateStaticConstructor(fileClass);

                // register other ruby source files in assembly so that they can be loaded if requested
                foreach (SOURCEFILE f in files) {
                    if (f.fileClass == null)
                        f.CreateClassForFile(context, File.stripExtension(f.location.file));

                    // Ruby.Runtime.Program.AddProgram(filename, fileClass);
                    cctor.ldstr(File.stripExtension(f.location.file));
                    cctor.ldtoken(f.fileClass);
                    cctor.call(Runtime.SystemType.GetTypeFromHandle);
                    cctor.call(Runtime.Program.AddProgram);
                }

                // Load(Object.ruby_top_self, null);
                cctor.ldsfld(Runtime.Object.ruby_top_self);
                cctor.ldnull();
                cctor.call(LoadMethod.Method);
                cctor.pop();
                cctor.ret();

                // }
                cctor.Close();
            }

            return fileClass;
        }


        private static MethodDef GenerateOptionsMethod(CodeGenContext context, List<KeyValuePair<string, object>> runtime_options) {
            // internal void SetOptions(string[] args) {
            CodeGenContext SetOptions = context.CreateModuleMethod("SetOptions", PrimitiveType.Void, new Param(ParamAttr.Default, "args", new PERWAPI.ZeroBasedArray(PrimitiveType.String)));

            SetOptions.ldarg("args");
            SetOptions.call(Runtime.Options.SetArgs);

            if (runtime_options != null)
                foreach (KeyValuePair<string, object> option in runtime_options) {
                    SetOptions.ldstr(option.Key);
                    if (option.Value == null)
                        SetOptions.ldnull();
                    else if (option.Value is string)
                        SetOptions.ldstr((string)option.Value);
                    else if (option.Value is int) {
                        SetOptions.ldc_i4((int)option.Value);
                        SetOptions.box(PrimitiveType.Int32);
                    } else if (option.Value is bool) {
                        if ((bool)option.Value)
                            SetOptions.PushTrue();
                        else
                            SetOptions.PushFalse();
                        SetOptions.box(PrimitiveType.Boolean);
                    } else
                        throw new System.NotImplementedException("unknown option");
                    SetOptions.call(Runtime.Options.SetRuntimeOption);
                }
            SetOptions.ret();
            SetOptions.Close();

            return SetOptions.Method;
        }


        private static void Execute(System.Reflection.Assembly Assembly, Frame caller, string filename) {
            if (Assembly.GetCustomAttributes(typeof(RubyAttribute), true).Length > 0)
                Load(Assembly, caller, filename);
            else
                Interop.CLRClass.LoadCLRAssembly(Assembly, caller);
        }

        internal static void LoadExisting(string filename, Frame caller) {
            string basename = File.stripExtension(filename);
            System.Type type = Program.programs[basename.Replace(@"\", "_").Replace(".", "_")];
            FieldInfo loadedField = type.GetField("loaded");
            if (loadedField != null)
                loadedField.SetValue(null, true);
            else {
                MethodInfo load = type.GetMethod("Load");
                try {
                    load.Invoke(null, new object[] { Ruby.Object.ruby_top_self, caller });
                } catch (System.Reflection.TargetInvocationException e) {
                    throw e.InnerException;
                }
            }
        }

        internal static void Load(System.Reflection.Assembly assembly, Frame caller, string filename) {
            try {
                System.Type[] types = assembly.GetTypes();
            } catch {
            }
            foreach (System.Type type in assembly.GetTypes()) {
                if (type.Namespace == "_Internal" && type.Name.StartsWith("SourceFile_"))
                    Program.AddProgram(type.Name.Substring(11).Replace(@"\\", @"\"), type);
            }

            object e_info = Eval.ruby_errinfo.value;
            try {
                LoadExisting(filename, caller);
            } catch (System.Reflection.TargetInvocationException exception) {
                throw exception.InnerException;
            } finally {
                Eval.ruby_errinfo.value = e_info;
            }
        }

        internal static void Load(PERWAPI.PEFile Assembly, Frame caller, string filename) {
            Load(CodeGenContext.Load(Assembly), caller, filename);
        }

        internal static void Execute(string path, Frame caller) {
            string fileName = new System.IO.FileInfo(path).FullName;
            System.Reflection.Assembly assembly = System.Reflection.Assembly.LoadFile(fileName);
            Execute(assembly, caller, path);
        }

        internal static void ExecuteMain(PERWAPI.PEFile Assembly, string[] args) {
            ExecuteMain(CodeGenContext.Load(Assembly), args);
        }


        internal static void ExecuteMain(string path, string[] args) {
            string fileName = new System.IO.FileInfo(path).FullName;
            System.Reflection.Assembly assembly = System.Reflection.Assembly.LoadFile(fileName);

            ExecuteMain(assembly, args);
        }

        private static void ExecuteMain(System.Reflection.Assembly Assembly, string[] args) {
            MethodInfo mainMethod = Assembly.GetModules(false)[0].GetMethod("Main");
            try {
                mainMethod.Invoke(null, new object[] { args });
            } catch (System.Reflection.TargetInvocationException exception) {
                throw exception.InnerException;
            }
        }

        public CodeCompileUnit ToCodeCompileUnit() {
            CodeCompileUnit unit = new CodeCompileUnit();

            CodeNamespace defaultNS = new CodeNamespace();

            unit.Namespaces.Add(defaultNS);

            for (Node n = body; n != null; n = n.nd_next)
                if (n is CLASS) {
                    defaultNS.Types.Add(((CLASS)n).ToCodeTypeDeclaration());
                    break; // only care about the first class
                }

            return unit;
        }
    }
}
