using Mono.Cecil;
using Mono.Cecil.Cil;

// Patches MailClient.Protocols.Matrix.MatrixClient.Deserialize<T> to normalise /sync JSON before
// the source-generated System.Text.Json deserializer runs. See
// il-patches/MailClient.Wine/MatrixJsonDefaults.cs and
// reports/emclient11-matrix-beeper-sync-findings.md.
//
// Usage:
//   matrix-sync-json-patcher <input MailClient.Protocols.Matrix.dll> <MailClient.Wine.dll> <output MailClient.Protocols.Matrix.dll>

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: matrix-sync-json-patcher <input MailClient.Protocols.Matrix.dll> <MailClient.Wine.dll> <output MailClient.Protocols.Matrix.dll>");
    return 1;
}

string inputPath = args[0];
string helperPath = args[1];
string outputPath = args[2];

var resolver = new DefaultAssemblyResolver();
string? inputDir = Path.GetDirectoryName(Path.GetFullPath(inputPath));
if (inputDir != null) resolver.AddSearchDirectory(inputDir);
string? helperDir = Path.GetDirectoryName(Path.GetFullPath(helperPath));
if (helperDir != null) resolver.AddSearchDirectory(helperDir);

using var helperModule = ModuleDefinition.ReadModule(helperPath, new ReaderParameters { AssemblyResolver = resolver });
TypeDefinition helperType = helperModule.GetType("MailClient.Wine.MatrixJsonDefaults")
    ?? throw new Exception("MatrixJsonDefaults type not found in MailClient.Wine.dll");
MethodDefinition helperMethod = helperType.Methods.Single(m => m.Name == "EnsureSyncRequiredFields" && m.HasGenericParameters);

using var module = ModuleDefinition.ReadModule(inputPath, new ReaderParameters { ReadWrite = true, AssemblyResolver = resolver });

TypeDefinition matrixClient = module.GetType("MailClient.Protocols.Matrix.MatrixClient")
    ?? throw new Exception("MailClient.Protocols.Matrix.MatrixClient not found");

MethodDefinition deserialize = matrixClient.Methods.Single(m =>
    m.Name == "Deserialize" &&
    m.HasGenericParameters &&
    m.GenericParameters.Count == 1 &&
    m.Parameters.Count == 2 &&
    m.Parameters[0].ParameterType.FullName == "System.String")
    ?? throw new Exception("MatrixClient.Deserialize<T>(string, JsonTypeInfo<T>) not found");

bool alreadyPatched = deserialize.Body.Instructions.Any(i =>
    i.OpCode == OpCodes.Call &&
    i.Operand is MethodReference mr &&
    mr.Name == "EnsureSyncRequiredFields" &&
    mr.DeclaringType.FullName == "MailClient.Wine.MatrixJsonDefaults");

if (alreadyPatched)
{
    Console.WriteLine("MatrixClient.Deserialize already calls MatrixJsonDefaults.EnsureSyncRequiredFields -- skipping");
}
else
{
    MethodReference importedHelper = module.ImportReference(helperMethod);
    var genericHelper = new GenericInstanceMethod(importedHelper);
    genericHelper.GenericArguments.Add(deserialize.GenericParameters[0]);

    ILProcessor il = deserialize.Body.GetILProcessor();
    Instruction first = deserialize.Body.Instructions[0];
    il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
    il.InsertBefore(first, il.Create(OpCodes.Call, genericHelper));
    il.InsertBefore(first, il.Create(OpCodes.Starg, deserialize.Parameters[0]));
    Console.WriteLine($"Patched MatrixClient.Deserialize<{deserialize.GenericParameters[0].Name}>: inserted MatrixJsonDefaults.EnsureSyncRequiredFields call");
}

string? outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
if (outputDir != null) Directory.CreateDirectory(outputDir);
module.Write(outputPath);
Console.WriteLine($"Wrote {outputPath}");
return 0;
