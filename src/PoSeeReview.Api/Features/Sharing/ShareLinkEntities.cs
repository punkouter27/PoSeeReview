using System.Security.Cryptography;
using Azure;
using Azure.Data.Tables;
using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Api.Features.Sharing;

/// <summary>
/// Key construction and code minting for short links.
/// <para>
/// Two row shapes share one table. The forward row answers "what does this code point at",
/// which is the read on the hot path; the reverse row answers "does this place already have a
/// code", which is what keeps a place from accumulating a new short link every time someone
/// taps Share.
/// </para>
/// </summary>
internal static class ShareLinkKeys
{
    /// <summary>Partition holding <c>code → place</c> rows.</summary>
    public const string CodePartition = "SHORTCODE";

    /// <summary>Partition holding <c>place → code</c> rows.</summary>
    public const string PlacePartition = "SHORTPLACE";

    /// <summary>
    /// Seven characters from an alphabet with no <c>0/O</c>, <c>1/I/l</c> pairs.
    /// <para>
    /// These get read aloud, retyped off a screenshot, and printed in small type. Ambiguous
    /// glyphs are the difference between a link that works and one that 404s for reasons the
    /// person retyping it cannot see.
    /// </para>
    /// </summary>
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyzACDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Length of a minted code. 53^7 is far more space than this app will ever need.</summary>
    public const int CodeLength = 7;

    /// <summary>
    /// Mints a code with a cryptographic RNG rather than <see cref="Random"/>. Not because a
    /// short link is a secret, but because a guessable sequence would let anyone enumerate every
    /// comic anyone has ever shared.
    /// </summary>
    public static string NewCode() =>
        RandomNumberGenerator.GetString(Alphabet, CodeLength);

    /// <summary>True when a candidate could have been minted here — cheap rejection before a read.</summary>
    public static bool IsWellFormed(string? code) =>
        !string.IsNullOrEmpty(code)
        && code.Length == CodeLength
        && code.All(Alphabet.Contains);

    /// <summary>
    /// Strips the characters Table Storage rejects in keys. Place ids are alphanumeric in
    /// practice, but this key is built from request input and must not produce a malformed row.
    /// </summary>
    public static string RowKeyFor(PlaceId placeId)
    {
        var cleaned = new string(placeId.Value
            .Where(c => !char.IsControl(c) && c is not ('/' or '\\' or '#' or '?'))
            .ToArray());

        return string.IsNullOrWhiteSpace(cleaned) ? "unknown" : cleaned;
    }
}

/// <summary>Forward row: the code is the key, so resolving a short link is a point read.</summary>
public class ShareCodeEntity : ITableEntity
{
    public string PartitionKey { get; set; } = ShareLinkKeys.CodePartition;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string PlaceId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Reverse row: one code per place, so Share is idempotent.</summary>
public class SharePlaceEntity : ITableEntity
{
    public string PartitionKey { get; set; } = ShareLinkKeys.PlacePartition;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Code { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
