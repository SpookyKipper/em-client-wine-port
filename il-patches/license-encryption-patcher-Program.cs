using Mono.Cecil;
using Mono.Cecil.Cil;

// Patches eM Client 11's Licensing.Encryption.dll for Wine's broken native crypto:
//
// 1. v2 license format (ECDSA P-256/SHA-256, IEEE P1363 signature): DecryptAndVerify's
//    constructor calls ECDsa.Create() and then ImportFromPem. Under Wine the import fails in
//    CNG/bcrypt (CryptographicException 0x80090029 from ECCng.ImportKeyBlob), so the app never
//    gets a usable verification key and silently treats the license as invalid. Redirect that
//    one call to BouncyCastlePatch.EcdsaPatch.Create(), whose ManagedEcdsa implements the same
//    ECDsa surface in pure managed code (ImportFromPem then works, VerifyData uses BouncyCastle).
//
// 2. Older RSA-encrypted license formats: DecryptAndVerifyStringV1 / DecryptStringV0 call
//    RSACryptoServiceProvider.Decrypt(data, fOAEP: true), the same Wine RSA-OAEP defect already
//    fixed for eM Client 10 (reports/license-activation-oaep-findings.md). Remove the `true`
//    argument and retarget the call to BouncyCastlePatch.OaepPatch.OaepSha1Decrypt(RSA, byte[]),
//    whose stack shape is [rsa, data] -- exactly what remains after dropping the bool.
//
// Usage:
//   license-encryption-patcher <input Licensing.Encryption.dll> <MailClient.Licensing.BouncyCastlePatch.dll> <output Licensing.Encryption.dll>

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: license-encryption-patcher <input Licensing.Encryption.dll> <MailClient.Licensing.BouncyCastlePatch.dll> <output Licensing.Encryption.dll>");
    return 1;
}

string inputPath = args[0];
string patchDllPath = args[1];
string outputPath = args[2];

var resolver = new DefaultAssemblyResolver();
string? inputDir = Path.GetDirectoryName(Path.GetFullPath(inputPath));
if (inputDir != null) resolver.AddSearchDirectory(inputDir);
string? patchDir = Path.GetDirectoryName(Path.GetFullPath(patchDllPath));
if (patchDir != null) resolver.AddSearchDirectory(patchDir);

using var patchModule = ModuleDefinition.ReadModule(patchDllPath, new ReaderParameters { AssemblyResolver = resolver });
TypeDefinition patchType = patchModule.GetType("MailClient.Licensing.BouncyCastlePatch.EcdsaPatch")
    ?? throw new Exception("EcdsaPatch type not found in patch assembly");
MethodDefinition createMethod = patchType.Methods.Single(m => m.Name == "Create");

TypeDefinition oaepType = patchModule.GetType("MailClient.Licensing.BouncyCastlePatch.OaepPatch")
    ?? throw new Exception("OaepPatch type not found in patch assembly");
MethodDefinition oaepDecryptMethod = oaepType.Methods.Single(m => m.Name == "OaepSha1Decrypt");

using var module = ModuleDefinition.ReadModule(inputPath, new ReaderParameters { ReadWrite = true, AssemblyResolver = resolver });
MethodReference importedCreate = module.ImportReference(createMethod);
MethodReference importedOaep = module.ImportReference(oaepDecryptMethod);

TypeDefinition decryptAndVerify = module.GetType("Licensing.Encryption.DecryptAndVerify")
    ?? throw new Exception("Licensing.Encryption.DecryptAndVerify not found");

// --- 1. ECDsa.Create() -> EcdsaPatch.Create() -------------------------------------------------

int ecdsaPatched = 0;
int ecdsaAlreadyPatched = 0;
foreach (MethodDefinition ctor in decryptAndVerify.Methods.Where(m => m.IsConstructor))
{
    foreach (Instruction instr in ctor.Body.Instructions)
    {
        if (instr.OpCode != OpCodes.Call || instr.Operand is not MethodReference mr || mr.Name != "Create") continue;
        if (mr.DeclaringType.FullName == "System.Security.Cryptography.ECDsa")
        {
            instr.Operand = importedCreate;
            ecdsaPatched++;
        }
        else if (mr.DeclaringType.FullName == "MailClient.Licensing.BouncyCastlePatch.EcdsaPatch")
        {
            ecdsaAlreadyPatched++;
        }
    }
}

if (ecdsaPatched == 0 && ecdsaAlreadyPatched == 1)
{
    Console.WriteLine("ECDsa::Create already redirected to EcdsaPatch::Create -- skipping");
}
else if (ecdsaPatched != 1)
{
    throw new Exception($"expected exactly 1 ECDsa::Create() call in DecryptAndVerify's constructors, found {ecdsaPatched} (already patched: {ecdsaAlreadyPatched})");
}

// --- 2. RSA-OAEP calls ------------------------------------------------------------------------

int rsaPatched = 0;
foreach (MethodDefinition method in decryptAndVerify.Methods)
{
    if (!method.HasBody) continue;
    ILProcessor il = method.Body.GetILProcessor();
    var instructions = method.Body.Instructions;

    var rsaCalls = instructions.Where(instr =>
        instr.OpCode == OpCodes.Callvirt &&
        instr.Operand is MethodReference mr &&
        mr.Name == "Decrypt" &&
        mr.DeclaringType.FullName == "System.Security.Cryptography.RSACryptoServiceProvider" &&
        mr.Parameters.Count == 2 &&
        mr.Parameters[0].ParameterType.FullName == "System.Byte[]" &&
        mr.Parameters[1].ParameterType.FullName == "System.Boolean").ToList();

    foreach (Instruction instr in rsaCalls)
    {
        Instruction? boolLoad = instr.Previous;
        if (boolLoad == null || boolLoad.OpCode != OpCodes.Ldc_I4_1)
        {
            throw new Exception($"{method.Name}: RSACryptoServiceProvider::Decrypt call is not immediately preceded by ldc.i4.1 (fOAEP: true), found {boolLoad?.OpCode}");
        }

        // boolLoad is being deleted -- retarget anything that branches to it or uses it as an
        // exception-handler boundary to the surviving decrypt instruction. Same guard as the
        // eM Client 10 OAEP patcher (see CLAUDE.md's IL-patching lessons).
        foreach (Instruction candidate in instructions)
        {
            if (candidate.Operand == boolLoad)
            {
                candidate.Operand = instr;
            }
        }
        foreach (ExceptionHandler eh in method.Body.ExceptionHandlers)
        {
            if (eh.TryStart == boolLoad) eh.TryStart = instr;
            if (eh.TryEnd == boolLoad) eh.TryEnd = instr;
            if (eh.HandlerStart == boolLoad) eh.HandlerStart = instr;
            if (eh.HandlerEnd == boolLoad) eh.HandlerEnd = instr;
            if (eh.FilterStart == boolLoad) eh.FilterStart = instr;
        }

        il.Remove(boolLoad);
        instr.OpCode = OpCodes.Call;
        instr.Operand = importedOaep;
        rsaPatched++;
    }
}

if (rsaPatched == 0)
{
    int oaepAlready = decryptAndVerify.Methods
        .Where(m => m.HasBody)
        .SelectMany(m => m.Body.Instructions)
        .Count(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference mr &&
                    mr.DeclaringType.FullName == "MailClient.Licensing.BouncyCastlePatch.OaepPatch" &&
                    mr.Name == "OaepSha1Decrypt");
    if (oaepAlready >= 1)
    {
        Console.WriteLine($"RSA-OAEP calls already redirected to OaepPatch ({oaepAlready} site(s)) -- skipping");
    }
    else
    {
        throw new Exception("expected at least 1 RSACryptoServiceProvider::Decrypt(byte[], bool) call, found 0");
    }
}

string? outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
if (outputDir != null) Directory.CreateDirectory(outputDir);
module.Write(outputPath);
Console.WriteLine($"Patched {ecdsaPatched} ECDsa::Create call(s) -> EcdsaPatch::Create");
Console.WriteLine($"Patched {rsaPatched} RSA-OAEP Decrypt call(s) -> OaepPatch::OaepSha1Decrypt");
Console.WriteLine($"Wrote {outputPath}");
return 0;
