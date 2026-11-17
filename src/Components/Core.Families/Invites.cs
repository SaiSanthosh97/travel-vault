using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TravelVault.Core.Families;

/// <summary>
/// In-person invite, shown as a QR code. Contains the family key: anyone scanning it joins immediately.
/// Safe because it is only shown on screen with both people present, and it expires quickly.
/// </summary>
public sealed record InPersonInvite(
    string InviteId,
    byte[] JoinSecret,
    string FamilyId,
    string FamilyName,
    string RelayServer,
    string RelayTopic,
    int KeyEpoch,
    byte[] FamilyKey,
    string InviterMemberId,
    byte[] InviterSigningKey,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Remote invite, sent as text (WhatsApp, SMS). Contains NO family key — only how to reach the admin and a
/// one-time secret. The joiner sends its keys to the admin; after both compare the safety code, the admin
/// approves and sends the family key sealed to the joiner's device.
/// </summary>
public sealed record RemoteInvite(
    string InviteId,
    string FamilyId,
    string FamilyName,
    string RelayServer,
    string JoinTopic,
    byte[] JoinSecret,
    string AdminMemberId,
    byte[] AdminSigningKey,
    byte[] AdminAgreementKey,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Turns invites into short text/QR payloads and back. Format: <c>tv1:q:</c> or <c>tv1:r:</c> + base64url(JSON).
/// The prefix lets the app recognise an invite pasted from anywhere, and the version allows future formats.
/// </summary>
public static class InviteCodec
{
    private const string InPersonPrefix = "tv1:q:";
    private const string RemotePrefix = "tv1:r:";

    public static readonly TimeSpan InPersonLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RemoteDefaultLifetime = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Encode(InPersonInvite invite) => InPersonPrefix + Base64Url(JsonSerializer.SerializeToUtf8Bytes(invite, Json));

    public static string Encode(RemoteInvite invite) => RemotePrefix + Base64Url(JsonSerializer.SerializeToUtf8Bytes(invite, Json));

    /// <summary>A friendly message to paste into WhatsApp, with the code on its own line.</summary>
    public static string ShareText(RemoteInvite invite) =>
        $"Join our family \"{invite.FamilyName}\" in Travel Vault.\nOpen Travel Vault → Join a family → paste this code:\n\n{Encode(invite)}";

    /// <summary>Finds and decodes an invite inside any text (the whole pasted message is fine).</summary>
    /// <returns>An <see cref="InPersonInvite"/>, a <see cref="RemoteInvite"/>, or null if none / expired / malformed.</returns>
    public static object? Decode(string? text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var word in text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (word.StartsWith(InPersonPrefix, StringComparison.Ordinal))
                {
                    var invite = JsonSerializer.Deserialize<InPersonInvite>(FromBase64Url(word[InPersonPrefix.Length..]), Json);
                    return invite is not null && invite.ExpiresAt > now && Valid(invite) ? invite : null;
                }

                if (word.StartsWith(RemotePrefix, StringComparison.Ordinal))
                {
                    var invite = JsonSerializer.Deserialize<RemoteInvite>(FromBase64Url(word[RemotePrefix.Length..]), Json);
                    return invite is not null && invite.ExpiresAt > now && Valid(invite) ? invite : null;
                }
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                return null;
            }
        }

        return null;
    }

    public static byte[] NewJoinSecret() => RandomNumberGenerator.GetBytes(16);

    private static bool Valid(InPersonInvite i) =>
        i.FamilyKey is { Length: 32 } && i.JoinSecret is { Length: 16 } && IsHttps(i.RelayServer) && i.RelayTopic.Length is > 10 and <= 64 && i.InviterSigningKey.Length > 0;

    private static bool Valid(RemoteInvite i) =>
        i.JoinSecret is { Length: 16 } && IsHttps(i.RelayServer) && i.JoinTopic.Length is > 10 and <= 64 && i.AdminAgreementKey.Length > 0;

    private static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }

    /// <summary>For logs and UI: never print secrets.</summary>
    public static string Describe(object invite) => invite switch
    {
        InPersonInvite q => $"In-person invite to {q.FamilyName}",
        RemoteInvite r => $"Remote invite to {r.FamilyName}",
        _ => "Invite",
    };
}
