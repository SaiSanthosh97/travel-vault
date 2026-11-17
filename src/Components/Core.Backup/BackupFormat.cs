using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using TravelVault.Core.Common;
using TravelVault.Core.Documents;
using TravelVault.Core.Families;
using TravelVault.Core.Security;
using TravelVault.Core.Sync;
using TravelVault.Core.Travelers;
using TravelVault.Core.Trips;

namespace TravelVault.Core.Backup;

/// <summary>Everything needed to rebuild this phone's vault on another phone.</summary>
public sealed class BackupManifest
{
    public DateTimeOffset CreatedAt { get; set; }

    public required string MemberId { get; set; }

    public required string DisplayName { get; set; }

    public required byte[] SigningPrivateKey { get; set; }

    public required byte[] AgreementPrivateKey { get; set; }

    public List<FamilyBackup> Families { get; set; } = [];
}

public sealed class FamilyBackup
{
    public required Family Family { get; set; }

    /// <summary>Family keys by epoch (old epochs too, so older data can still be read).</summary>
    public Dictionary<int, byte[]> Keys { get; set; } = [];

    public List<Traveler> Travelers { get; set; } = [];

    public List<TravelDocument> Documents { get; set; } = [];

    public List<Trip> Trips { get; set; } = [];

    /// <summary>The signed change log, so this phone can keep answering family state requests after a restore.</summary>
    public List<SignedChange> Changes { get; set; } = [];

    /// <summary>This device's message counter, so family phones don't mistake new messages for replays.</summary>
    public long MySeq { get; set; }

    public bool Removed { get; set; }
}

/// <summary>
/// The backup file: a zip with
/// <c>header.json</c> (plain: format, salt, rounds — needed to derive the key),
/// <c>manifest</c> (AES-256-GCM: gzip(JSON of <see cref="BackupManifest"/>)) and
/// <c>files/&lt;id&gt;</c> (AES-256-GCM: each original file).
/// The key is PBKDF2-SHA256(recovery code, salt, 600 000 rounds). Each entry is sealed with its own label, so
/// entries can't be swapped. Without the recovery code the file is unreadable.
/// </summary>
public static class BackupFormat
{
    public const string FormatName = "travelvault-backup";
    public const int FormatVersion = 1;
    private const string HeaderEntry = "header.json";
    private const string ManifestEntry = "manifest";
    private const string FilesFolder = "files/";

    private sealed record Header(string Format, int Version, string Salt, int Iterations, DateTimeOffset CreatedAt);

    public static async Task WriteAsync(
        Stream output,
        BackupManifest manifest,
        IEnumerable<string> fileIds,
        Func<string, Task<byte[]?>> readFile,
        RecoveryCode code,
        int iterations = BackupKey.DefaultIterations,
        CancellationToken cancellationToken = default)
    {
        var salt = RandomNumberGenerator.GetBytes(BackupKey.SaltSize);
        var key = BackupKey.Derive(code, salt, iterations);
        try
        {
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            await WriteEntryAsync(zip, HeaderEntry, JsonSerializer.SerializeToUtf8Bytes(new Header(FormatName, FormatVersion, Convert.ToBase64String(salt), iterations, manifest.CreatedAt), JsonDefaults.Options), CompressionLevel.NoCompression);

            var json = Gzip(JsonSerializer.SerializeToUtf8Bytes(manifest, JsonDefaults.Options));
            await WriteEntryAsync(zip, ManifestEntry, AeadBox.Seal(key, json, "backup/manifest"), CompressionLevel.NoCompression);

            foreach (var id in fileIds.Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await readFile(id) is { } content)
                {
                    // Encrypted data doesn't compress; store as is.
                    await WriteEntryAsync(zip, FilesFolder + id, AeadBox.Seal(key, content, "backup/file/" + id), CompressionLevel.NoCompression);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <exception cref="WrongRecoveryCodeException">The code doesn't open this backup.</exception>
    /// <exception cref="InvalidDataException">Not a Travel Vault backup, or damaged.</exception>
    public static async Task<OpenedBackup> OpenAsync(Stream input, RecoveryCode code, CancellationToken cancellationToken = default)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("This file is not a Travel Vault backup.");
        }

        var headerBytes = await ReadEntryAsync(zip, HeaderEntry) ?? throw new InvalidDataException("This file is not a Travel Vault backup.");
        var header = JsonSerializer.Deserialize<Header>(headerBytes, JsonDefaults.Options);
        if (header is not { Format: FormatName } || header.Version > FormatVersion || header.Iterations is < 1 or > 10_000_000)
        {
            throw new InvalidDataException("This backup was made by a newer app version or is damaged.");
        }

        var key = BackupKey.Derive(code, Convert.FromBase64String(header.Salt), header.Iterations);
        var sealedManifest = await ReadEntryAsync(zip, ManifestEntry) ?? throw new InvalidDataException("The backup is damaged.");
        var manifestBytes = AeadBox.Open(key, sealedManifest, "backup/manifest") ?? throw new WrongRecoveryCodeException();
        var manifest = JsonSerializer.Deserialize<BackupManifest>(Gunzip(manifestBytes), JsonDefaults.Options) ?? throw new InvalidDataException("The backup is damaged.");
        return new OpenedBackup(zip, key, manifest);
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, byte[] data, CompressionLevel level)
    {
        var entry = zip.CreateEntry(name, level);
        await using var stream = await entry.OpenAsync();
        await stream.WriteAsync(data);
    }

    internal static async Task<byte[]?> ReadEntryAsync(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null)
        {
            return null;
        }

        await using var stream = await entry.OpenAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    internal const string FilesPrefix = FilesFolder;
}

/// <summary>An opened (decrypted) backup. Files are read one at a time, so big vaults don't need much memory.</summary>
public sealed class OpenedBackup(ZipArchive zip, byte[] key, BackupManifest manifest) : IDisposable
{
    public BackupManifest Manifest { get; } = manifest;

    public async Task<byte[]?> ReadFileAsync(string fileVersionId) =>
        await BackupFormat.ReadEntryAsync(zip, BackupFormat.FilesPrefix + fileVersionId) is { } sealedFile
            ? AeadBox.Open(key, sealedFile, "backup/file/" + fileVersionId)
            : null;

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(key);
        zip.Dispose();
    }
}

public sealed class WrongRecoveryCodeException() : Exception("This recovery code doesn't open the backup. Check it and try again.");
