namespace FamilyDashboard.Web.Services.Photos;

public class LocalFolderPhotoService(IConfiguration configuration, ILogger<LocalFolderPhotoService> logger) : IPhotoService
{
    private static readonly string[] SupportedExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    public async Task SaveAsync(Stream content, string fileName, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
        {
            throw new InvalidDataException("Only JPG, PNG, and WebP images can be uploaded.");
        }

        var photoDir = GetPhotoDirectory();
        Directory.CreateDirectory(photoDir);
        var safeName = $"{Path.GetFileNameWithoutExtension(Path.GetRandomFileName())}{extension}";
        var destination = Path.Combine(photoDir, safeName);

        await using var output = File.Create(destination);
        await content.CopyToAsync(output, cancellationToken);
    }

    public Task<List<string>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var photoDir = GetPhotoDirectory();

        var results = new List<string>();

        if (!Directory.Exists(photoDir))
        {
            logger.LogWarning("Photo directory {PhotoDir} does not exist yet", photoDir);
            return Task.FromResult(results);
        }

        foreach (var file in Directory.EnumerateFiles(photoDir, "*.*", SearchOption.AllDirectories))
        {
            if (!SupportedExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
            {
                continue;
            }

            var relative = Path.GetRelativePath(photoDir, file).Replace(Path.DirectorySeparatorChar, '/');
            results.Add($"/photos/{relative}");
        }

        // Shuffle so the slideshow order changes on each rescan rather than always
        // going alphabetically / by folder.
        var random = Random.Shared;
        for (var i = results.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (results[i], results[j]) = (results[j], results[i]);
        }

        return Task.FromResult(results);
    }

    private string GetPhotoDirectory()
    {
        var photoDir = configuration["PhotoDirectory"];
        if (string.IsNullOrWhiteSpace(photoDir))
        {
            photoDir = Path.Combine(AppContext.BaseDirectory, "App_Data", "Photos");
        }
        else if (!Path.IsPathRooted(photoDir))
        {
            photoDir = Path.Combine(AppContext.BaseDirectory, photoDir);
        }

        return photoDir;
    }
}
