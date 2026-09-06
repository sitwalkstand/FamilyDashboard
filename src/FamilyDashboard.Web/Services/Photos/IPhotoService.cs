namespace FamilyDashboard.Web.Services.Photos;

public interface IPhotoService
{
    /// <summary>Rescans the mounted photo folder and returns web-servable paths under /photos/...</summary>
    Task<List<string>> ScanAsync(CancellationToken cancellationToken = default);
}
