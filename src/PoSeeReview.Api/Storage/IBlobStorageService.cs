namespace PoSeeReview.Api.Storage;

/// <summary>
/// Service for uploading and managing comic images in Azure Blob Storage
/// </summary>
public interface IBlobStorageService
{
    /// <summary>
    /// Uploads a comic image to Azure Blob Storage
    /// </summary>
    /// <param name="comicId">Unique comic identifier</param>
    /// <param name="imageBytes">PNG image bytes</param>
    /// <returns>Public URL of the uploaded image</returns>
    /// <exception cref="ArgumentException">If comicId is empty or imageBytes is null/empty</exception>
    Task<string> UploadComicImageAsync(string comicId, byte[] imageBytes);

    /// <summary>
    /// Deletes a comic image from Azure Blob Storage if it exists
    /// </summary>
    /// <param name="comicId">Unique comic identifier whose blob should be removed</param>
    Task DeleteComicImageAsync(string comicId);

    /// <summary>
    /// Deletes a blob by its URL. Best effort: a failure is logged, never thrown.
    /// </summary>
    /// <param name="blobUrl">Full URL of the blob to delete</param>
    Task DeleteBlobAsync(string blobUrl);

    /// <summary>
    /// Generates a fresh SAS URL for an existing blob whose token may have expired.
    /// </summary>
    /// <param name="existingBlobUrl">Current (potentially expired) SAS URL</param>
    /// <returns>New SAS URL valid for the full SAS token duration</returns>
    Task<string> RefreshSasUrlAsync(string existingBlobUrl);

    /// <summary>
    /// Opens a read stream over an existing comic blob.
    /// <para>
    /// Exists so the API can re-serve a comic from its own origin. The browser's
    /// <c>download</c> attribute is ignored on cross-origin hrefs, and the storage account
    /// sends no CORS headers, so a blob URL handed straight to the page can only ever be
    /// opened in a tab — never saved. Streaming it back through the app makes it same-origin,
    /// and the save works.
    /// </para>
    /// </summary>
    /// <param name="blobUrl">Full URL (with or without SAS) of the blob to read</param>
    /// <param name="cancellationToken">Cancels the read when the client disconnects</param>
    /// <returns>The stream, or <c>null</c> when the blob no longer exists</returns>
    Task<Stream?> OpenComicImageStreamAsync(string blobUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a blob physically exists in storage.
    /// Used to detect leaderboard entries whose blobs were purged by the cleanup service.
    /// </summary>
    /// <param name="blobUrl">Full URL (with or without SAS) of the blob to check</param>
    Task<bool> BlobExistsAsync(string blobUrl);

    /// <summary>Writes (or overwrites) a named blob in the comics container — the audio comics.</summary>
    Task UploadNamedAsync(string name, byte[] bytes, string contentType, CancellationToken cancellationToken = default);

    /// <summary>The named blob's bytes, or null when there is none.</summary>
    Task<byte[]?> ReadNamedAsync(string name, CancellationToken cancellationToken = default);
}
