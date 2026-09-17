namespace FamilyDashboard.Web.Services.Photos;

public interface IPhotoService
{
    /// <summary>Rescans the mounted photo folder and returns web-servable paths under /photos/...</summary>
    Task<List<string>> ScanAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves an uploaded image into the configured photo folder.</summary>
    Task SaveAsync(Stream content, string fileName, CancellationToken cancellationToken = default);
}
