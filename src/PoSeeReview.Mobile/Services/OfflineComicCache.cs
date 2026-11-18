using System.Text.Json;
using PoSeeReview.Shared.Dtos;

namespace PoSeeReview.Mobile.Services;

public sealed class OfflineComicCache : IDisposable
{
    private readonly string _boardPath = Path.Combine(FileSystem.AppDataDirectory, "poseereview-board.json");
    private readonly string _comicDir = Path.Combine(FileSystem.AppDataDirectory, "poseereview-comics");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OfflineComicCache()
    {
        Directory.CreateDirectory(_comicDir);
    }

    public void Dispose() => _gate.Dispose();

    public async Task SaveLeaderboardAsync(LeaderboardResponse board, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var temp = _boardPath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(board), ct).ConfigureAwait(false);
            File.Move(temp, _boardPath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LeaderboardResponse?> LoadLeaderboardAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_boardPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<LeaderboardResponse>(await File.ReadAllTextAsync(_boardPath, ct).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> SaveComicImageAsync(string placeId, byte[] bytes, string extension, CancellationToken ct = default)
    {
        var safeId = string.Concat(placeId.Where(char.IsLetterOrDigit));
        var file = Path.Combine(_comicDir, $"{safeId}.{extension.TrimStart('.')}");
        await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false);
        return file;
    }
}
