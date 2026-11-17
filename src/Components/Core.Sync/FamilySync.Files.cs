using TravelVault.Core.Families;
using TravelVault.Core.Security;
using TravelVault.Core.Vault;

namespace TravelVault.Core.Sync;

/// <summary>
/// Moving original files between phones. A file is encrypted with a key derived from the family key and the file
/// id, split into chunks below ntfy.sh's 2 MB attachment limit, and each chunk is sent as an attachment with a
/// signed message describing it. Receivers collect chunks (they may arrive over several cycles), verify the whole
/// file's SHA-256, and store it with a fresh local key. Attachments expire after ~3 h, so a phone that missed them
/// asks again, and any phone that has the file answers.
/// </summary>
public sealed partial class FamilySync
{
    /// <summary>"View" on a missing file: ask the family right now (not waiting for the next cycle).</summary>
    public async Task RequestAsync(string familyId, string fileVersionId, CancellationToken cancellationToken = default)
    {
        if (await _families.GetAsync(familyId, cancellationToken) is not { } family || !Admitted(family))
        {
            return;
        }

        var ctx = new Cycle(family, _relays.For(family.RelayServer));
        await PublishAsync(ctx, Kinds.FileRequest, new FileRequestBody([fileVersionId]), cancellationToken);
        await _settings.SetTimeAsync(Keys.FileRequested(fileVersionId), _session.Now, cancellationToken);
    }

    private async Task SendRequestedFilesAsync(Cycle ctx, CancellationToken ct)
    {
        foreach (var fileId in ctx.FilesAsked)
        {
            var sent = await _settings.GetTimeAsync(Keys.FileSent(fileId), ct);
            if (sent is not null && _session.Now - sent < _options.FileResendEvery)
            {
                continue; // several phones asked for the same file: send it once
            }

            await SendFileAsync(ctx, fileId, ct);
        }
    }

    private async Task RequestMissingFilesAsync(Cycle ctx, CancellationToken ct)
    {
        var present = await _fileKeys.PresentAsync(ctx.Family.Id, ct);
        var missing = new List<string>();
        foreach (var doc in await _documents.ListAsync(ctx.Family.Id, ct))
        {
            foreach (var file in doc.Files.Where(f => !present.Contains(f.Id) && f.Size <= _options.MaxSyncedFileSize))
            {
                var asked = await _settings.GetTimeAsync(Keys.FileRequested(file.Id), ct);
                if (asked is null || _session.Now - asked > _options.FileRequestEvery)
                {
                    missing.Add(file.Id);
                }
            }
        }

        if (missing.Count == 0)
        {
            return;
        }

        foreach (var batch in missing.Chunk(60))
        {
            await PublishAsync(ctx, Kinds.FileRequest, new FileRequestBody([.. batch]), ct);
        }

        foreach (var id in missing)
        {
            await _settings.SetTimeAsync(Keys.FileRequested(id), _session.Now, ct);
        }
    }

    private async Task SendFileAsync(Cycle ctx, string fileVersionId, CancellationToken ct)
    {
        var content = await _documentService.ReadLocalFileAsync(ctx.Family.Id, fileVersionId, ct);
        if (content is null || content.LongLength > _options.MaxSyncedFileSize)
        {
            return;
        }

        var familyKey = await CurrentKeyAsync(ctx.Family, ct);
        var fileKey = FileKey(familyKey, fileVersionId);
        var sha = Convert.ToHexStringLower(FileVault.Sha256(content));
        var count = Math.Max(1, (int)Math.Ceiling(content.Length / (double)_options.ChunkSize));
        for (var i = 0; i < count; i++)
        {
            var part = content.AsSpan(i * _options.ChunkSize, Math.Min(_options.ChunkSize, content.Length - i * _options.ChunkSize));
            var box = AeadBox.Seal(fileKey, part, ChunkLabel(ctx.Family.Id, fileVersionId, i, count));
            await PublishAsync(ctx, Kinds.FileChunk, new FileChunkBody(fileVersionId, i, count, content.LongLength, sha), ct, attachment: box);
        }

        await _settings.SetTimeAsync(Keys.FileSent(fileVersionId), _session.Now, ct);
    }

    private async Task HandleChunkAsync(Cycle ctx, FileChunkBody chunk, Uri? attachment, int epoch, CancellationToken ct)
    {
        if (attachment is null || !SafeId(chunk.File) || chunk.Count is < 1 or > 32 || chunk.Index < 0 || chunk.Index >= chunk.Count
            || chunk.Size > _options.MaxSyncedFileSize || await _fileKeys.GetAsync(chunk.File, ct) is not null)
        {
            return;
        }

        var folder = Path.Combine(_incomingFolder, ctx.Family.Id, chunk.File);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{chunk.Index}.{chunk.Count}.{epoch}.part");
        if (!File.Exists(path))
        {
            byte[] box;
            try
            {
                box = await ctx.Relay.DownloadAsync(attachment, ct);
            }
            catch (HttpRequestException)
            {
                return; // expired (ntfy.sh keeps files ~3 h); we'll ask again
            }

            await File.WriteAllBytesAsync(path, box, ct);
        }

        await TryAssembleAsync(ctx, chunk, folder, ct);
    }

    private async Task TryAssembleAsync(Cycle ctx, FileChunkBody chunk, string folder, CancellationToken ct)
    {
        var parts = new Dictionary<int, string>();
        foreach (var path in Directory.GetFiles(folder, $"*.{chunk.Count}.*.part"))
        {
            var name = Path.GetFileName(path).Split('.');
            if (int.TryParse(name[0], out var index))
            {
                parts[index] = path;
            }
        }

        if (parts.Count < chunk.Count)
        {
            return;
        }

        using var whole = new MemoryStream();
        for (var i = 0; i < chunk.Count; i++)
        {
            var epoch = int.Parse(Path.GetFileName(parts[i]).Split('.')[2], System.Globalization.CultureInfo.InvariantCulture);
            var familyKey = await KeyAsync(ctx.Family.Id, epoch, ct);
            var box = await File.ReadAllBytesAsync(parts[i], ct);
            if (familyKey is null || AeadBox.Open(FileKey(familyKey, chunk.File), box, ChunkLabel(ctx.Family.Id, chunk.File, i, chunk.Count)) is not { } plain)
            {
                Directory.Delete(folder, recursive: true); // tampered or wrong: discard and ask again later
                return;
            }

            whole.Write(plain);
        }

        var content = whole.ToArray();
        Directory.Delete(folder, recursive: true);
        if (content.LongLength != chunk.Size || Convert.ToHexStringLower(FileVault.Sha256(content)) != chunk.Sha256.ToLowerInvariant())
        {
            return; // integrity check failed
        }

        await _documentService.StoreReceivedFileAsync(ctx.Family.Id, chunk.File, content, ct);
        ctx.Received++;
        if (await _documents.FindByFileHashAsync(ctx.Family.Id, chunk.Sha256, ct) is { } doc)
        {
            await _events.PublishAsync(new VaultChange(ctx.Family.Id, RecordType.Document, doc.Id, FromSync: true), ct);
        }
    }

    private static byte[] FileKey(byte[] familyKey, string fileVersionId) => KeyDerivation.Derive(familyKey, "file/" + fileVersionId);

    private static string ChunkLabel(string familyId, string fileId, int index, int count) => $"chunk/{familyId}/{fileId}/{index}/{count}";
}
