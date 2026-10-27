using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PoSeeReview.Api.Storage;
using PoSeeReview.Integration.TestFixtures;
using Xunit;

namespace PoSeeReview.Integration.Storage;

[Trait("Tier", "Integration")]
[Trait("Domain", "Storage")]
[Trait("Suite", "CriticalPath")]
public class BlobStorageTests : IClassFixture<AzuriteFixture>
{
    private readonly AzuriteFixture _fixture;
    private readonly BlobServiceClient _blobServiceClient;

    public BlobStorageTests(AzuriteFixture fixture)
    {
        _fixture = fixture;
        _fixture.EnsureAzuriteAvailable();
        _blobServiceClient = new BlobServiceClient(_fixture.ConnectionString);
    }

    /// <summary>
    /// The service, not the SDK, against a real Azurite URL. Azurite paths carry the account as
    /// their first segment (<c>/devstoreaccount1/comics/{blob}</c>); the resolver used to skip
    /// exactly one segment and look up <c>comics/{blob}</c> inside the comics container, so every
    /// local download, existence check and delete 404'd while the JSON said the comic was there.
    /// A second upload under the same id must replace the first: remastering redraws in place.
    /// </summary>
    [Fact]
    public async Task BlobStorageService_ResolvesAzuriteSasUrl_ForReadOverwriteExistsAndDelete()
    {
        _fixture.EnsureAzuriteAvailable();
        await _blobServiceClient.GetBlobContainerClient("comics").CreateIfNotExistsAsync();
        var service = new BlobStorageService(
            _blobServiceClient,
            Options.Create(new AzureStorageOptions()),
            NullLogger<BlobStorageService>.Instance);
        var comicId = $"test-comic-{Guid.NewGuid()}";

        var url = await service.UploadComicImageAsync(comicId, PngBytes(1024));
        Assert.StartsWith("http", url);
        Assert.True(await service.BlobExistsAsync(url));

        var redrawn = PngBytes(2048);
        url = await service.UploadComicImageAsync(comicId, redrawn);
        Assert.Equal(redrawn, await ReadAsync(service, url));

        await service.DeleteBlobAsync(url);
        Assert.False(await service.BlobExistsAsync(url));
    }

    private static async Task<byte[]> ReadAsync(BlobStorageService service, string url)
    {
        await using var stream = await service.OpenComicImageStreamAsync(url);
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>A PNG signature and random bytes: enough for the service to name it <c>.png</c>.</summary>
    private static byte[] PngBytes(int size)
    {
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        return bytes;
    }
}
