using System.Collections.Concurrent;
using System.ClientModel.Primitives;
using System.Security.Cryptography;
using AgentChat.Foundry;
using Microsoft.AspNetCore.StaticFiles;
using OpenAI.Responses;

namespace AgentChat.Services;

public sealed record AgentInputFile(string FileName, string ContentType, BinaryData Content);

public sealed record GeneratedFileLink(
    string FileName,
    string ContentType,
    long Size,
    string Url);

public sealed class AgentFileService
{
    private const int DefaultMaxFileCount = 10;
    private const long DefaultMaxFileBytes = 25L * 1024 * 1024;
    private const long DefaultMaxRequestBytes = 50L * 1024 * 1024;
    private const long DefaultMaxCachedBytes = 100L * 1024 * 1024;
    private static readonly TimeSpan DefaultDownloadLifetime = TimeSpan.FromMinutes(15);
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly ConcurrentDictionary<string, CachedDownload> _downloads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingFiles> _pendingFiles = new(StringComparer.Ordinal);
    private readonly int _maxFileCount;
    private readonly long _maxFileBytes;
    private readonly long _maxRequestBytes;
    private readonly long _maxCachedBytes;
    private readonly TimeSpan _downloadLifetime;
    private readonly object _cacheGate = new();
    private long _cachedBytes;

    public AgentFileService(IConfiguration configuration)
    {
        _maxFileCount = configuration.GetValue("Files:MaxCount", DefaultMaxFileCount);
        _maxFileBytes = configuration.GetValue("Files:MaxFileBytes", DefaultMaxFileBytes);
        _maxRequestBytes = configuration.GetValue("Files:MaxRequestBytes", DefaultMaxRequestBytes);
        _maxCachedBytes = configuration.GetValue("Files:MaxCachedBytes", DefaultMaxCachedBytes);
        _downloadLifetime = TimeSpan.FromMinutes(
            configuration.GetValue("Files:DownloadLifetimeMinutes", DefaultDownloadLifetime.TotalMinutes));
    }

    public async Task<IReadOnlyList<AgentInputFile>> ReadFormFilesAsync(
        IEnumerable<IFormFile> formFiles,
        CancellationToken cancellationToken)
    {
        var files = new List<AgentInputFile>();
        foreach (var formFile in formFiles)
        {
            if (formFile.Length > _maxFileBytes)
            {
                throw new InvalidOperationException(
                    $"File '{SafeFileName(formFile.FileName)}' exceeds the {_maxFileBytes / 1024 / 1024} MB limit.");
            }

            await using var stream = formFile.OpenReadStream();
            using var buffer = new MemoryStream(capacity: checked((int)formFile.Length));
            await stream.CopyToAsync(buffer, cancellationToken);
            files.Add(new AgentInputFile(
                SafeFileName(formFile.FileName),
                NormalizeContentType(formFile.ContentType, formFile.FileName),
                BinaryData.FromBytes(buffer.ToArray())));
        }

        Validate(files);
        return files;
    }

    public IReadOnlyList<AgentInputFile> Validate(IEnumerable<AgentInputFile> inputFiles)
    {
        var files = inputFiles.ToList();
        if (files.Count > _maxFileCount)
            throw new InvalidOperationException($"A message can include at most {_maxFileCount} files.");

        long total = 0;
        foreach (var file in files)
        {
            var size = file.Content.ToMemory().Length;
            if (size > _maxFileBytes)
                throw new InvalidOperationException($"File '{SafeFileName(file.FileName)}' exceeds the {_maxFileBytes / 1024 / 1024} MB limit.");
            total += size;
        }

        if (total > _maxRequestBytes)
            throw new InvalidOperationException($"Attachments exceed the {_maxRequestBytes / 1024 / 1024} MB per-message limit.");

        return files;
    }

    public ResponseItem CreateUserMessage(string? text, IReadOnlyList<AgentInputFile> inputFiles)
    {
        var files = Validate(inputFiles);
        var parts = new List<ResponseContentPart>(files.Count + 1);
        if (!string.IsNullOrWhiteSpace(text))
            parts.Add(ResponseContentPart.CreateInputTextPart(text));

        parts.AddRange(files.Select(file =>
            ResponseContentPart.CreateInputFilePart(file.Content, file.ContentType, file.FileName)));

        if (parts.Count == 0)
            throw new InvalidOperationException("A message or at least one attachment is required.");

        return ResponseItem.CreateUserMessageItem(parts);
    }

    public async Task<GeneratedFileLink> CaptureContainerFileAsync(
        FoundryClient foundry,
        ContainerFileCitationMessageAnnotation annotation,
        Uri publicBaseUri,
        CancellationToken cancellationToken)
    {
        var bytes = await DownloadContainerFileAsync(foundry, annotation, cancellationToken);

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var fileName = SafeFileName(annotation.Filename);
        var contentType = NormalizeContentType(null, fileName);
        var cached = new CachedDownload(
            fileName,
            contentType,
            bytes,
            DateTimeOffset.UtcNow.Add(_downloadLifetime));

        lock (_cacheGate)
        {
            RemoveExpiredEntriesLocked();
            if (_cachedBytes + cached.Content.LongLength > _maxCachedBytes)
                throw new InvalidOperationException("The generated-file download cache is full. Try again in a few minutes.");
            if (!_downloads.TryAdd(token, cached))
                throw new InvalidOperationException("Could not allocate a generated-file download token.");
            _cachedBytes += cached.Content.LongLength;
        }

        var path = $"/api/files/{token}/{Uri.EscapeDataString(fileName)}";
        return new GeneratedFileLink(
            fileName,
            contentType,
            cached.Content.LongLength,
            new Uri(publicBaseUri, path).ToString());
    }

    public bool TryGetDownload(string token, out GeneratedFileDownload download)
    {
        lock (_cacheGate)
        {
            download = null!;
            if (!_downloads.TryGetValue(token, out var cached))
                return false;

            if (cached.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                RemoveDownloadLocked(token, cached);
                return false;
            }

            download = new GeneratedFileDownload(cached.FileName, cached.ContentType, cached.Content);
            return true;
        }
    }

    public void StorePendingFiles(string conversationId, IReadOnlyList<AgentInputFile> inputFiles)
    {
        var files = Validate(inputFiles);
        var size = files.Sum(file => (long)file.Content.ToMemory().Length);
        lock (_cacheGate)
        {
            RemoveExpiredEntriesLocked();
            _pendingFiles.TryGetValue(conversationId, out var previous);
            if (files.Count == 0)
            {
                if (previous is not null && _pendingFiles.TryRemove(conversationId, out _))
                    _cachedBytes -= previous.Size;
                return;
            }

            var projectedSize = _cachedBytes - (previous?.Size ?? 0) + size;
            if (projectedSize > _maxCachedBytes)
                throw new InvalidOperationException("The temporary file cache is full. Try again in a few minutes.");

            _pendingFiles[conversationId] = new PendingFiles(
                files,
                size,
                DateTimeOffset.UtcNow.Add(_downloadLifetime));
            _cachedBytes = projectedSize;
        }
    }

    public IReadOnlyList<AgentInputFile> TakePendingFiles(string conversationId)
    {
        lock (_cacheGate)
        {
            if (!_pendingFiles.TryRemove(conversationId, out var pending))
                return [];

            _cachedBytes -= pending.Size;
            return pending.ExpiresAt <= DateTimeOffset.UtcNow ? [] : pending.Files;
        }
    }

    public static Uri PublicBaseUri(HttpRequest? request)
    {
        if (request is null)
            return new Uri("https://localhost");

        var scheme = request.Host.Host is "localhost" or "127.0.0.1"
            ? request.Scheme
            : "https";
        return new Uri($"{scheme}://{request.Host}{request.PathBase}/");
    }

    private async Task<byte[]> DownloadContainerFileAsync(
        FoundryClient foundry,
        ContainerFileCitationMessageAnnotation annotation,
        CancellationToken cancellationToken)
    {
        var options = new RequestOptions
        {
            BufferResponse = false,
            CancellationToken = cancellationToken
        };
        var result = await foundry.ProjectOpenAI.GetContainerClient()
            .DownloadContainerFileAsync(annotation.ContainerId, annotation.FileId, options);
        using var response = result.GetRawResponse();
        if (response.Headers.TryGetValue("Content-Length", out var contentLength) &&
            long.TryParse(contentLength, out var declaredLength) &&
            declaredLength > _maxFileBytes)
        {
            throw new InvalidOperationException(
                $"Generated file '{SafeFileName(annotation.Filename)}' exceeds the {_maxFileBytes / 1024 / 1024} MB download limit.");
        }

        await using var input = response.ContentStream;
        if (input is null)
            throw new InvalidOperationException($"Generated file '{SafeFileName(annotation.Filename)}' had no content.");

        using var output = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            total += read;
            if (total > _maxFileBytes)
            {
                throw new InvalidOperationException(
                    $"Generated file '{SafeFileName(annotation.Filename)}' exceeds the {_maxFileBytes / 1024 / 1024} MB download limit.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return output.ToArray();
    }

    private void RemoveExpiredEntriesLocked()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _downloads)
        {
            if (entry.Value.ExpiresAt <= now)
                RemoveDownloadLocked(entry.Key, entry.Value);
        }

        foreach (var entry in _pendingFiles)
        {
            if (entry.Value.ExpiresAt <= now &&
                _pendingFiles.TryRemove(new KeyValuePair<string, PendingFiles>(entry.Key, entry.Value)))
            {
                _cachedBytes -= entry.Value.Size;
            }
        }
    }

    private void RemoveDownloadLocked(string token, CachedDownload cached)
    {
        if (_downloads.TryRemove(new KeyValuePair<string, CachedDownload>(token, cached)))
            _cachedBytes -= cached.Content.LongLength;
    }

    private static string SafeFileName(string? fileName)
    {
        var safe = Path.GetFileName(fileName);
        return string.IsNullOrWhiteSpace(safe) ? "attachment" : safe;
    }

    private static string NormalizeContentType(string? contentType, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType.Split(';', 2)[0].Trim();

        return ContentTypes.TryGetContentType(fileName, out var inferred)
            ? inferred
            : "application/octet-stream";
    }

    private sealed record CachedDownload(
        string FileName,
        string ContentType,
        byte[] Content,
        DateTimeOffset ExpiresAt);

    private sealed record PendingFiles(
        IReadOnlyList<AgentInputFile> Files,
        long Size,
        DateTimeOffset ExpiresAt);
}

public sealed record GeneratedFileDownload(string FileName, string ContentType, byte[] Content);
