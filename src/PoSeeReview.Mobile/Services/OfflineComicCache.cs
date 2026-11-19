using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PoSeeReview.Shared.Dtos;

namespace PoSeeReview.Mobile.Services;

/// <summary>
/// On-device copies so the app still works without signal: the Hall of Fame, the last nearby
/// search, and the last <see cref="MaxComics"/> comics opened (metadata + image). Same rules as the
/// web PWA's image cache: a comic is never shown past its <c>ExpiresAt</c>, and a 404/451 from the
/// server evicts it, so a takedown also reaches offline copies the next time the phone is online.
/// </summary>
public sealed class OfflineComicCache : IDisposable
{
    private const int MaxComics = 30;
    private readonly string _root = FileSystem.AppDataDirectory;
    private readonly string _comicDir = Path.Combine(FileSystem.AppDataDirectory, "poseereview-comics");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OfflineComicCache() => Directory.CreateDirectory(_comicDir);

    public void Dispose() => _gate.Dispose();

    public Task SaveLeaderboardAsync(LeaderboardResponse board, CancellationToken ct = default)
        => WriteJsonAsync(Path.Combine(_root, "poseereview-board.json"), board, MobileJsonContext.Default.LeaderboardResponse, ct);

    public Task<LeaderboardResponse?> LoadLeaderboardAsync(CancellationToken ct = default)
        => ReadJsonAsync(Path.Combine(_root, "poseereview-board.json"), MobileJsonContext.Default.LeaderboardResponse, ct);

    public Task SaveNearbyAsync(SavedNearby nearby, CancellationToken ct = default)
        => WriteJsonAsync(Path.Combine(_root, "poseereview-nearby.json"), nearby, MobileJsonContext.Default.SavedNearby, ct);

    public Task<SavedNearby?> LoadNearbyAsync(CancellationToken ct = default)
        => ReadJsonAsync(Path.Combine(_root, "poseereview-nearby.json"), MobileJsonContext.Default.SavedNearby, ct);

    /// <summary>Stores a comic and its image; returns the image's local path.</summary>
    public async Task<string> SaveComicAsync(ComicDto comic, byte[] image, string extension, CancellationToken ct = default)
    {
        var stem = Stem(comic.PlaceId);
        DeleteImages(stem);
        var imagePath = Path.Combine(_comicDir, $"{stem}.{extension.TrimStart('.')}");
        await WriteBytesAsync(imagePath, image, ct).ConfigureAwait(false);
        await WriteJsonAsync(Path.Combine(_comicDir, $"{stem}.json"), comic, MobileJsonContext.Default.ComicDto, ct).ConfigureAwait(false);
        Prune();
        return imagePath;
    }

    /// <summary>The saved comic and image path, or null when absent or expired (expired is deleted).</summary>
    public async Task<(ComicDto Comic, string ImagePath)?> LoadComicAsync(string placeId, CancellationToken ct = default)
    {
        var stem = Stem(placeId);
        var comic = await ReadJsonAsync(Path.Combine(_comicDir, $"{stem}.json"), MobileJsonContext.Default.ComicDto, ct).ConfigureAwait(false);
        var image = Directory.EnumerateFiles(_comicDir, $"{stem}.*").FirstOrDefault(f => !f.EndsWith(".json", StringComparison.Ordinal));
        if (comic is null || image is null || comic.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            Evict(placeId);
            return null;
        }

        return (comic, image);
    }

    /// <summary>Drops a comic the server no longer serves (404 expired/removed, 451 withheld).</summary>
    public void Evict(string placeId)
    {
        var stem = Stem(placeId);
        DeleteImages(stem);
        File.Delete(Path.Combine(_comicDir, $"{stem}.json"));
    }

    private void DeleteImages(string stem)
    {
        foreach (var file in Directory.EnumerateFiles(_comicDir, $"{stem}.*").Where(f => !f.EndsWith(".json", StringComparison.Ordinal)))
        {
            File.Delete(file);
        }
    }

    private void Prune()
    {
        var stale = new DirectoryInfo(_comicDir).GetFiles("*.json")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(MaxComics);
        foreach (var json in stale)
        {
            DeleteImages(Path.GetFileNameWithoutExtension(json.Name));
            json.Delete();
        }
    }

    // Place ids are Google's opaque [A-Za-z0-9_-] strings; keep only those so a crafted id can't
    // escape the folder.
    private static string Stem(string placeId) => string.Concat(placeId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'));

    private async Task WriteJsonAsync<T>(string path, T value, JsonTypeInfo<T> type, CancellationToken ct)
        => await WriteBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(value, type), ct).ConfigureAwait(false);

    // Write-then-rename, so a crash mid-write never leaves a half file to be read back.
    private async Task WriteBytesAsync(string path, byte[] bytes, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T?> ReadJsonAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken ct)
        where T : class
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync(stream, type, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
