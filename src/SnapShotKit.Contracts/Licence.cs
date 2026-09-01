using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnapShotKit.Contracts;

/// <summary>
/// What somebody bought.
///
/// Everything here is signed, so nothing here can be edited without the signature falling apart.
/// That is the whole point of the arrangement: the application can be certain of these numbers
/// without asking anybody, which is what lets it work on a train.
/// </summary>
public sealed class Licence
{
    /// <summary>The identifier the seller knows this licence by. Also what a support email quotes.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    /// <summary>
    /// Who it was sold to, shown in the application.
    ///
    /// Not enforcement, and not meant to be. A name on the screen is simply an awkward thing to
    /// pass around, which turns out to deter far more casual sharing than any check does.
    /// </summary>
    [JsonPropertyName("to")]
    public string To { get; init; } = "";

    [JsonPropertyName("email")]
    public string Email { get; init; } = "";

    [JsonPropertyName("issued")]
    public DateOnly Issued { get; init; }

    /// <summary>
    /// How many machines it covers.
    ///
    /// Nothing counts them yet. It is signed in from the first key ever issued because a number
    /// that is not in the payload cannot be added to it later without reissuing every key that has
    /// already been sold, and this one costs a dozen bytes to carry until it is wanted.
    /// </summary>
    [JsonPropertyName("seats")]
    public int Seats { get; init; }

    /// <summary>
    /// The last version, by date, that this licence entitles its owner to. Null means forever,
    /// which is what is being sold today.
    ///
    /// Here for the same reason as <see cref="Seats"/>: it is the other thing that cannot be
    /// retrofitted onto keys already in the wild, and carrying it costs nothing while it is null.
    /// </summary>
    [JsonPropertyName("updates")]
    public DateOnly? Updates { get; init; }
}

/// <summary>Why a key was not accepted. Every one of these is a different sentence to the user.</summary>
public enum LicenceProblem
{
    /// <summary>No problem. The licence is good.</summary>
    None,

    /// <summary>Nothing was entered.</summary>
    Missing,

    /// <summary>Not a SnapShotKit key at all, so probably the wrong thing off the clipboard.</summary>
    NotAKey,

    /// <summary>A key from a later scheme than this build understands. Update rather than despair.</summary>
    UnknownVersion,

    /// <summary>The right shape, but the contents did not survive the journey. Usually a copy that clipped a character.</summary>
    Malformed,

    /// <summary>Well formed and not signed by us.</summary>
    Forged
}

/// <summary>
/// Reads and writes licence keys.
///
/// A key is a signed statement, verified against a public key built into the application, and
/// checking one involves no network at all. This is deliberate and it is the load-bearing decision
/// in the whole licensing arrangement: a machine that cannot reach the seller must still be able to
/// install the software and run it, so the seller is never in the path of the question "may I run".
/// Counting how many machines a licence is on is a separate job for a separate day, and one that
/// only ever informs.
///
/// Both directions live here rather than the reader here and the writer in whatever tool issues
/// keys. A format described in two places is a format that will eventually be described two
/// different ways, and the failure would show up as keys that verify nowhere.
///
/// The shape is <c>SSK1.payload.signature</c>, both parts base64url. The prefix is there so that a
/// key is recognisable on sight in a support email, and so that a later scheme can say so plainly
/// rather than looking like corruption.
/// </summary>
public static class LicenceKey
{
    /// <summary>Marks the scheme. A later one gets SSK2 and this build will say so rather than guess.</summary>
    public const string Prefix = "SSK1";

    /// <summary>
    /// The public half of the signing key, as base64 SubjectPublicKeyInfo.
    ///
    /// Empty until a keypair exists. Generate one once, keep the private half offline and off every
    /// machine that faces the internet, and paste the public half here:
    ///
    /// <code>
    /// using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    /// Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());  // goes here
    /// Convert.ToBase64String(key.ExportPkcs8PrivateKey());       // goes in a safe, never in git
    /// </code>
    ///
    /// Publishing this is fine and expected. It verifies signatures; it cannot make them.
    /// </summary>
    public const string PublicKey = "";

    /// <summary>
    /// P-256, because it is in the base class library and needs no dependency. .NET 10 has no
    /// standalone Ed25519, which would otherwise have been the obvious choice.
    /// </summary>
    static readonly ECCurve Curve = ECCurve.NamedCurves.nistP256;

    /// <summary>
    /// Fixed-width r and s rather than DER. It is a few bytes shorter, and more to the point it is
    /// one length always, so a key is one length always.
    /// </summary>
    const DSASignatureFormat SignatureFormat = DSASignatureFormat.IeeeP1363FixedFieldConcatenation;

    /// <summary>Reads a key against the built-in public key, which is what the application does.</summary>
    public static Licence? Read(string? key, out LicenceProblem problem)
    {
        if (PublicKey.Length == 0)
        {
            // A build with no public key in it cannot check anything, and quietly accepting or
            // quietly refusing would both be worse than saying so. This is a mistake in the build,
            // not in whatever the user typed.
            throw new InvalidOperationException(
                $"{nameof(LicenceKey)}.{nameof(PublicKey)} is empty, so this build cannot verify licences.");
        }

        using var verifier = ECDsa.Create(Curve);
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);

        return Read(key, verifier, out problem);
    }

    /// <summary>
    /// Reads a key against a given public key.
    ///
    /// Separate from the overload above so that the tool which issues keys, and anything checking
    /// this code still works, can supply a key of their own without one being compiled in.
    /// </summary>
    public static Licence? Read(string? key, ECDsa publicKey, out LicenceProblem problem)
    {
        problem = LicenceProblem.None;

        // Whitespace is what an email client and a careless paste leave behind, and neither is the
        // user's fault. Nothing else is forgiven.
        var trimmed = key?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            problem = LicenceProblem.Missing;
            return null;
        }

        var parts = trimmed.Split('.');

        if (parts.Length != 3)
        {
            problem = LicenceProblem.NotAKey;
            return null;
        }

        if (parts[0] != Prefix)
        {
            // A key that announces a scheme tells the user something useful; anything else is just
            // not one of ours.
            problem = parts[0].StartsWith("SSK", StringComparison.Ordinal)
                ? LicenceProblem.UnknownVersion
                : LicenceProblem.NotAKey;

            return null;
        }

        if (!TryDecode(parts[1], out var payload) || !TryDecode(parts[2], out var signature))
        {
            problem = LicenceProblem.Malformed;
            return null;
        }

        // Before the contents are looked at, and not after. Everything below this line is trusted
        // precisely because this line passed.
        if (!publicKey.VerifyData(payload, signature, HashAlgorithmName.SHA256, SignatureFormat))
        {
            problem = LicenceProblem.Forged;
            return null;
        }

        Licence? licence;

        try
        {
            licence = JsonSerializer.Deserialize(payload, LicenceJson.Default.Licence);
        }
        catch (JsonException)
        {
            // Signed and unreadable means something went wrong at the issuing end, which is worth
            // telling the user about in the same breath as a truncated paste: either way the key
            // they hold is not usable and the answer is to ask for another.
            problem = LicenceProblem.Malformed;
            return null;
        }

        if (licence is null || licence.Id.Length == 0 || licence.Seats < 1)
        {
            problem = LicenceProblem.Malformed;
            return null;
        }

        return licence;
    }

    /// <summary>
    /// Signs a licence into a key. Needs the private half, so this only ever runs where that lives.
    /// </summary>
    public static string Write(Licence licence, ECDsa privateKey)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(licence, LicenceJson.Default.Licence);
        var signature = privateKey.SignData(payload, HashAlgorithmName.SHA256, SignatureFormat);

        return $"{Prefix}.{Base64Url.EncodeToString(payload)}.{Base64Url.EncodeToString(signature)}";
    }

    static bool TryDecode(string value, out byte[] decoded)
    {
        try
        {
            decoded = Base64Url.DecodeFromChars(value);
            return true;
        }
        catch (FormatException)
        {
            decoded = [];
            return false;
        }
    }
}

/// <summary>
/// Serialisation worked out at build time rather than by reflection, because this assembly is
/// compiled ahead of time and reflection is exactly what that cannot do.
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Licence))]
internal sealed partial class LicenceJson : JsonSerializerContext;
