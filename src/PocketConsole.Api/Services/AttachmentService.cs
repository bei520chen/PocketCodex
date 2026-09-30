using System.Text.Json;
using PocketConsole.Api.Models;

namespace PocketConsole.Api.Services;

public sealed class AttachmentService
{
    private const long MaxFileSize = 20 * 1024 * 1024;
    private readonly string _uploadDirectory;

    public AttachmentService(IWebHostEnvironment environment)
    {
        _uploadDirectory = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", ".runtime", "uploads"));
        Directory.CreateDirectory(_uploadDirectory);
    }

    public async Task<AttachmentRo> SaveAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length <= 0) throw new ArgumentException("附件不能为空。");
        if (file.Length > MaxFileSize) throw new ArgumentException("单个附件不能超过 20 MB。");

        var id = Guid.NewGuid().ToString("N");
        var name = SanitizeName(file.FileName);
        var extension = Path.GetExtension(name);
        var storedPath = Path.Combine(_uploadDirectory, id + extension);
        var metadataPath = Path.Combine(_uploadDirectory, id + ".json");
        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
        var isImage = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

        await using (var output = new FileStream(storedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await file.CopyToAsync(output, cancellationToken);

        var metadata = new AttachmentMetadata(id, name, contentType, file.Length, isImage, storedPath);
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata), cancellationToken);
        return new AttachmentRo(id, name, contentType, file.Length, isImage);
    }

    public AttachmentInput Resolve(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("附件 ID 无效。");
        var metadataPath = Path.Combine(_uploadDirectory, id + ".json");
        if (!File.Exists(metadataPath)) throw new FileNotFoundException("附件不存在或已被清理。");
        var metadata = JsonSerializer.Deserialize<AttachmentMetadata>(File.ReadAllText(metadataPath))
            ?? throw new InvalidOperationException("附件元数据无效。");
        var path = Path.GetFullPath(metadata.Path);
        if (!path.StartsWith(_uploadDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new FileNotFoundException("附件不存在或路径无效。");
        return new AttachmentInput(metadata.Name, path, metadata.IsImage);
    }

    private static string SanitizeName(string value)
    {
        var name = Path.GetFileName(value).Trim();
        if (string.IsNullOrWhiteSpace(name)) name = "attachment";
        foreach (var character in Path.GetInvalidFileNameChars()) name = name.Replace(character, '_');
        return name.Length > 180 ? name[^180..] : name;
    }

    private sealed record AttachmentMetadata(string Id, string Name, string ContentType, long Size, bool IsImage, string Path);
}

public sealed record AttachmentInput(string Name, string Path, bool IsImage);
