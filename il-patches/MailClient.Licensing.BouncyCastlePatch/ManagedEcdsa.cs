using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.OpenSsl;

namespace MailClient.Licensing.BouncyCastlePatch;

// Pure-managed ECDsa implementation, used only for the license server's v2 signature check.
//
// eM Client 11's Licensing.Encryption.DecryptAndVerify creates a native ECDsa and imports the
// server's P-256 public key with ImportFromPem. Under Wine that import always fails inside
// CNG/bcrypt:
//
//   System.Security.Cryptography.CryptographicException: Unknown error (0x80090029)
//     at ECCng.ImportKeyBlob ... at ECDsaCng.ImportParameters
//     at ECAlgorithm.ImportSubjectPublicKeyInfo ... at PemKeyHelpers.ImportPem
//
// EcdsaKeyValidator then never even gets a chance to run, and the caller's v2 license verdict is
// a silent false (the surrounding code catches and returns false). This class replaces ONLY the
// ECDsa instance created in that constructor: the app keeps its own envelope parsing, signature
// format handling (IEEE P1363 r||s), strict UTF-8 decoding, and so on, and calls VerifyData on
// this object exactly as before. BouncyCastle's ECDsaSigner is pure managed code, so verification
// never touches Wine's broken CNG path.
public sealed class ManagedEcdsa : ECDsa
{
    private ECPublicKeyParameters? _publicKey;
    private ECParameters _parameters;

    public override int KeySize => 256;

    // Called by the app's existing constructor immediately after EcdsaPatch.Create().
    public override void ImportFromPem(ReadOnlySpan<char> input)
    {
        using var reader = new StringReader(input.ToString());
        object obj = new PemReader(reader).ReadObject();
        AsymmetricKeyParameter key = obj switch
        {
            AsymmetricKeyParameter ak => ak,
            Org.BouncyCastle.Asn1.X509.SubjectPublicKeyInfo spki
                => Org.BouncyCastle.Security.PublicKeyFactory.CreateKey(spki),
            _ => throw new CryptographicException("Unsupported ECDSA public key PEM."),
        };

        if (key is not ECPublicKeyParameters ec || ec.Parameters.N.BitLength != 256)
        {
            throw new CryptographicException("Expected an ECDSA P-256 public key.");
        }

        _publicKey = ec;
        _parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = ec.Q.AffineXCoord.GetEncoded(),
                Y = ec.Q.AffineYCoord.GetEncoded(),
            },
        };
    }

    // EcdsaKeyValidator.IsNistP256 checks exactly KeySize and this curve OID.
    public override ECParameters ExportParameters(bool includePrivateParameters)
    {
        if (includePrivateParameters)
        {
            throw new CryptographicException("This managed ECDsa only holds a public key.");
        }
        if (_publicKey is null)
        {
            throw new CryptographicException("No ECDSA public key imported.");
        }
        return _parameters;
    }

    public override void ImportParameters(ECParameters parameters) => throw new NotSupportedException();

    public override byte[] SignHash(byte[] hash) => throw new NotSupportedException();

    public override bool VerifyHash(byte[] hash, byte[] signature)
        => VerifyHash(new ReadOnlySpan<byte>(hash), new ReadOnlySpan<byte>(signature));

    public override bool VerifyHash(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> signature)
    {
        // DSASignatureFormat.IeeeP1363FixedFieldConcatenation for P-256 = 32-byte r || 32-byte s.
        if (_publicKey is null || signature.Length != 64)
        {
            return false;
        }

        var signer = new ECDsaSigner();
        signer.Init(forSigning: false, _publicKey);
        var r = new BigInteger(1, signature[..32].ToArray());
        var s = new BigInteger(1, signature[32..].ToArray());
        return signer.VerifySignature(hash.ToArray(), r, s);
    }

    protected override byte[] HashData(byte[] data, int offset, int count, HashAlgorithmName hashAlgorithm)
    {
        RequireSha256(hashAlgorithm);
        return SHA256.HashData(new ReadOnlySpan<byte>(data, offset, count));
    }

    protected override byte[] HashData(Stream data, HashAlgorithmName hashAlgorithm)
    {
        RequireSha256(hashAlgorithm);
        return SHA256.HashData(data);
    }

    protected override bool TryHashData(ReadOnlySpan<byte> data, Span<byte> destination, HashAlgorithmName hashAlgorithm, out int bytesWritten)
    {
        RequireSha256(hashAlgorithm);
        return SHA256.TryHashData(data, destination, out bytesWritten);
    }

    private static void RequireSha256(HashAlgorithmName hashAlgorithm)
    {
        if (hashAlgorithm != HashAlgorithmName.SHA256)
        {
            throw new CryptographicException($"Only SHA-256 is supported here, got {hashAlgorithm.Name}.");
        }
    }
}

public static class EcdsaPatch
{
    // Same stack shape as the native ECDsa.Create() call it replaces: a parameterless static
    // method returning ECDsa. ImportFromPem is then called on this instance as usual.
    public static ECDsa Create() => new ManagedEcdsa();
}
