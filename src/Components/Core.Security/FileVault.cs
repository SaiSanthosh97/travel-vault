using System.Security.Cryptography;

namespace TravelVault.Core.Security;

/// <summary>
/// Stores original documents encrypted in the app's private folder: one file per version, each with its own
/// random key (the key itself is stored, wrapped by the <see cref="KeyRing"/>, in the database).
/// A per-file key means a key leak exposes one file, and deleting a key securely "forgets" that file.
/// </summary>
public sealed class FileVault(string rootFolder)
{
    public string RootFolder { get; } = rootFolder;

    /// <summary>Encrypts and writes a file; returns the new random file key (caller stores it wrapped).</summary>
    public async Task<byte[]> WriteAsync(string familyId, string fileVersionId, byte[] plaintext, CancellationToken cancellationToken = default)
    {
        var key = AeadBox.NewKey();
        var box = AeadBox.Seal(key, plaintext, Label(familyId, fileVersionId));
        var path = PathFor(familyId, fileVersionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write to a temp file then move, so a crash never leaves a half-written file under the real name.
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, box, cancellationToken);
        File.Move(temp, path, overwrite: true);
        return key;
    }

    /// <returns>The plaintext, or null if the file is missing or doesn't open with this key (tampered/corrupt).</returns>
    public async Task<byte[]?> ReadAsync(string familyId, string fileVersionId, byte[] fileKey, CancellationToken cancellationToken = default)
    {
        var path = PathFor(familyId, fileVersionId);
        if (!File.Exists(path))
        {
            return null;
        }

        var box = await File.ReadAllBytesAsync(path, cancellationToken);
        return AeadBox.Open(fileKey, box, Label(familyId, fileVersionId));
    }

    public bool Exists(string familyId, string fileVersionId) => File.Exists(PathFor(familyId, fileVersionId));

    public void Delete(string familyId, string fileVersionId)
    {
        var path = PathFor(familyId, fileVersionId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void DeleteFamily(string familyId)
    {
        var folder = Path.Combine(RootFolder, Safe(familyId));
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    public long SizeOfFamily(string familyId)
    {
        var folder = Path.Combine(RootFolder, Safe(familyId));
        return Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateFiles().Sum(f => f.Length) : 0;
    }

    public static byte[] Sha256(byte[] data) => SHA256.HashData(data);

    private string PathFor(string familyId, string fileVersionId) =>
        Path.Combine(RootFolder, Safe(familyId), Safe(fileVersionId) + ".bin");

    private static string Label(string familyId, string fileVersionId) => $"file/{familyId}/{fileVersionId}";

    /// <summary>Ids come from synced data; never let one escape the vault folder ("../").</summary>
    private static string Safe(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? id
            : throw new ArgumentException($"Invalid id '{id}'.", nameof(id));
}
