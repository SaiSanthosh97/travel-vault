using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TravelVault.Core.Common;
using TravelVault.Core.Security;

namespace TravelVault.Core.Sync;

/// <summary>Message kinds on a family topic.</summary>
public static class Kinds
{
    public const string CheckIn = "checkin";
    public const string Changes = "changes";
    public const string StateRequest = "state-request";
    public const string FileRequest = "file-request";
    public const string FileChunk = "file-chunk";
    public const string Join = "join";
    public const string Rekey = "rekey";
}

/// <summary>
/// One message between family phones (before encryption). <see cref="Seq"/> increases per sending device, so a
/// replayed old message is recognised and dropped.
/// </summary>
public sealed record Envelope(int V, string FamilyId, string From, long Seq, string Sent, string Kind, JsonElement? Body = null)
{
    public const int CurrentVersion = 2;

    public T? BodyAs<T>() => Body is { } b ? b.Deserialize<T>(JsonDefaults.Options) : default;

    public static Envelope Create<T>(string familyId, string from, long seq, HybridTimestamp sent, string kind, T body) =>
        new(CurrentVersion, familyId, from, seq, sent.ToString(), kind, JsonSerializer.SerializeToElement(body, JsonDefaults.Options));
}

/// <summary>
/// A record change, signed by the member who made it. When another phone republishes it (answering a state request),
/// it forwards this exact object, so the original author's signature still proves who wrote it.
/// </summary>
public sealed record SignedChange(string Type, string Id, string FamilyId, string Version, string Author, string Payload, string Sig)
{
    public static SignedChange Create(string type, string id, string familyId, HybridTimestamp version, string author, string payload, DeviceKeyPair signer)
    {
        var unsigned = new SignedChange(type, id, familyId, version.ToString(), author, payload, "");
        return unsigned with { Sig = Convert.ToBase64String(signer.Sign(unsigned.SignedBytes())) };
    }

    public byte[] SignedBytes() => Encoding.UTF8.GetBytes($"{Type}|{Id}|{FamilyId}|{Version}|{Author}|{Payload}");

    public bool Verify(byte[] authorSigningKey)
    {
        try
        {
            return Signatures.Verify(authorSigningKey, SignedBytes(), Convert.FromBase64String(Sig));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public HybridTimestamp VersionValue => HybridTimestamp.TryParse(Version, out var v) ? v : HybridTimestamp.Zero;
}

public sealed record ChangesBody(List<SignedChange> Changes, string? ForRequest = null);

public sealed record StateRequestBody(string RequestId);

public sealed record FileRequestBody(List<string> Files);

public sealed record FileChunkBody(string File, int Index, int Count, long Size, string Sha256);

public sealed record CheckInBody(string Name);

/// <summary>A member's public identity as sent in join messages.</summary>
public sealed record MemberCard(string MemberId, string DisplayName, string SigningKey, string AgreementKey)
{
    public DevicePublicKeys Keys => new(Convert.FromBase64String(SigningKey), Convert.FromBase64String(AgreementKey));

    public static MemberCard Of(string memberId, string name, DevicePublicKeys keys) =>
        new(memberId, name, Convert.ToBase64String(keys.Signing), Convert.ToBase64String(keys.Agreement));
}

/// <summary>In-person join: "I scanned invite X; here are my keys; proof = HMAC(invite secret, my keys)".</summary>
public sealed record JoinBody(MemberCard Member, string InviteId, string Proof);

/// <summary>New family key for each remaining member, sealed to that member's device key.</summary>
public sealed record RekeyBody(int Epoch, string Topic, Dictionary<string, string> Keys, SignedChange Family);

/// <summary>
/// Turns envelopes into relay text and back. Pipeline: JSON → gzip → pad to a size bucket → AES-256-GCM with the
/// family message key → sign the ciphertext with the sender's device key.
/// Text form: <c>tv2.&lt;epoch&gt;.&lt;ciphertext&gt;.&lt;signature&gt;</c> (base64url). The epoch is readable so the
/// receiver knows which family key to try; everything else is encrypted.
/// </summary>
public static class WireCodec
{
    private const string Prefix = "tv2.";

    /// <summary>Padding buckets (bytes): hides how big a change is. The largest still fits ntfy's ~4 KB message limit.</summary>
    private static readonly int[] Buckets = [384, 768, 1536, 2600];

    public static int MaxPayload => Buckets[^1] - 4;

    public static byte[] MessageKey(byte[] familyKey) => KeyDerivation.Derive(familyKey, "msg");

    public static string Encode(Envelope envelope, byte[] familyKey, int epoch, DeviceKeyPair signer)
    {
        var compressed = Gzip(JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Options));
        var bucket = Buckets.FirstOrDefault(b => b >= compressed.Length + 4);
        if (bucket == 0)
        {
            throw new MessageTooLargeException();
        }

        var padded = new byte[bucket];
        BinaryPrimitives.WriteInt32BigEndian(padded, compressed.Length);
        compressed.CopyTo(padded, 4);

        var box = AeadBox.Seal(MessageKey(familyKey), padded, Label(envelope.FamilyId, epoch));
        var signature = signer.Sign(box);
        return $"{Prefix}{epoch}.{B64(box)}.{B64(signature)}";
    }

    /// <summary>Reads the epoch without decrypting, to choose the key.</summary>
    public static int? EpochOf(string text)
    {
        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var end = text.IndexOf('.', Prefix.Length);
        return end > 0 && int.TryParse(text.AsSpan(Prefix.Length, end - Prefix.Length), out var epoch) ? epoch : null;
    }

    /// <returns>The envelope plus the ciphertext and signature (for verifying the sender), or null if it can't be opened.</returns>
    public static (Envelope Envelope, byte[] Box, byte[] Signature)? Decode(string text, string familyId, byte[] familyKey)
    {
        var epoch = EpochOf(text);
        var parts = text.Split('.');
        if (epoch is null || parts.Length != 4)
        {
            return null;
        }

        try
        {
            var box = FromB64(parts[2]);
            var signature = FromB64(parts[3]);
            var padded = AeadBox.Open(MessageKey(familyKey), box, Label(familyId, epoch.Value));
            if (padded is null || padded.Length < 4)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(padded);
            if (length < 0 || length > padded.Length - 4)
            {
                return null;
            }

            var json = Gunzip(padded.AsSpan(4, length).ToArray());
            var envelope = JsonSerializer.Deserialize<Envelope>(json, JsonDefaults.Options);
            return envelope is { V: Envelope.CurrentVersion } && envelope.FamilyId == familyId ? (envelope, box, signature) : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Splits items into as few batches as possible, each small enough for one relay message. Compression isn't
    /// linear, so each batch is grown while it still fits (as in Use It Before's SyncCodec.Pack). Returns batches,
    /// not encoded text, so the caller encodes each final batch once with a real sequence number.
    /// </summary>
    /// <param name="fits">True when this batch encodes within the limit.</param>
    public static IReadOnlyList<IReadOnlyList<T>> Batch<T>(IReadOnlyList<T> items, Func<IReadOnlyList<T>, bool> fits)
    {
        var batches = new List<IReadOnlyList<T>>();
        var start = 0;
        while (start < items.Count)
        {
            if (!fits(items.Skip(start).Take(1).ToList()))
            {
                start++; // a single item too large for one message is skipped
                continue;
            }

            var count = 1;
            while (start + count < items.Count && fits(items.Skip(start).Take(count + 1).ToList()))
            {
                count++;
            }

            batches.Add(items.Skip(start).Take(count).ToList());
            start += count;
        }

        return batches;
    }

    /// <summary>True when the envelope encodes small enough for one relay message.</summary>
    public static bool Fits(Envelope envelope, byte[] familyKey, DeviceKeyPair signer)
    {
        try
        {
            return Encode(envelope, familyKey, 1, signer).Length <= MaxTextLength;
        }
        catch (MessageTooLargeException)
        {
            return false;
        }
    }

    /// <summary>ntfy.sh turns larger messages into attachments; stay below (same limit as NtfyRelay).</summary>
    public const int MaxTextLength = 3800;

    private static string Label(string familyId, int epoch) => $"msg/{familyId}/{epoch}";

    internal static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] FromB64(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        // Guard against decompression bombs: a real message is a few KB.
        var buffer = new byte[8192];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > 1_000_000)
            {
                throw new InvalidDataException("Message too large after decompression.");
            }
        }

        return output.ToArray();
    }
}

public sealed class MessageTooLargeException() : Exception("The message is too large for one relay message.");
