using System.Net.Http.Headers;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Core.Serialization;
using Microsoft.Agents.Extensions.Teams.Models;

namespace AgentChat.Services;

public interface ITeamsFileService
{
    bool SupportsNativeFiles(IActivity activity);
    Attachment CreateConsentCard(GeneratedFileLink file);
    Task<Attachment> UploadAsync(
        FileConsentCardResponse response,
        CancellationToken cancellationToken);
    void Discard(FileConsentCardResponse response);
}

public sealed class TeamsFileService : ITeamsFileService
{
    private static readonly string[] DefaultAllowedHosts =
    [
        "sharepoint.com",
        "sharepoint.us",
        "sharepoint.de",
        "sharepoint.cn",
        "sharepoint-mil.us",
        "up.1drv.com",
        "blob.core.windows.net"
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AgentFileService _files;
    private readonly string[] _allowedHosts;

    public TeamsFileService(
        IHttpClientFactory httpClientFactory,
        AgentFileService files,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _files = files;
        _allowedHosts = DefaultAllowedHosts
            .Concat(configuration.GetSection("OutboundHostValidator:Hosts").Get<string[]>() ?? [])
            .Select(NormalizeHost)
            .Where(host => host is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
    }

    public bool SupportsNativeFiles(IActivity activity)
        => string.Equals(activity.ChannelId, Channels.Msteams, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(activity.Conversation?.ConversationType, "personal", StringComparison.OrdinalIgnoreCase);

    public Attachment CreateConsentCard(GeneratedFileLink file)
    {
        if (string.IsNullOrWhiteSpace(file.Token))
            throw new InvalidOperationException("Generated file is missing its cache token.");

        var context = new TeamsGeneratedFileContext(file.Token);
        return new Attachment
        {
            Name = file.FileName,
            ContentType = FileConsentCard.ContentType,
            Content = new FileConsentCard(
                $"Download {file.FileName}",
                file.Size,
                context,
                context)
        };
    }

    public async Task<Attachment> UploadAsync(
        FileConsentCardResponse response,
        CancellationToken cancellationToken)
    {
        var token = ReadToken(response.Context);
        if (token is null || !_files.TryGetDownload(token, out var file))
            throw new InvalidOperationException("The generated file has expired. Run the request again.");

        var upload = response.UploadInfo
            ?? throw new InvalidOperationException("Teams did not provide a file upload session.");
        if (!IsAllowedUrl(upload.UploadUrl) || !IsAllowedUrl(upload.ContentUrl))
            throw new InvalidOperationException("Teams returned an unsupported file host.");
        if (file.Content.LongLength == 0)
            throw new InvalidOperationException("Teams cannot upload an empty generated file.");

        using var request = new HttpRequestMessage(HttpMethod.Put, upload.UploadUrl)
        {
            Content = new ByteArrayContent(file.Content)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
        request.Content.Headers.ContentLength = file.Content.LongLength;
        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(
            0,
            file.Content.LongLength - 1,
            file.Content.LongLength);

        using var client = _httpClientFactory.CreateClient(nameof(TeamsFileService));
        using var result = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        result.EnsureSuccessStatusCode();
        _files.RemoveDownload(token);

        return new Attachment
        {
            Name = file.FileName,
            ContentType = FileInfoCard.ContentType,
            ContentUrl = upload.ContentUrl,
            Content = new FileInfoCard
            {
                UniqueId = upload.UniqueId,
                FileType = upload.FileType
            }
        };
    }

    public void Discard(FileConsentCardResponse response)
    {
        var token = ReadToken(response.Context);
        if (token is not null)
            _files.RemoveDownload(token);
    }

    private static string? ReadToken(object? context)
    {
        if (context is TeamsGeneratedFileContext typed)
            return typed.Token;
        if (context is null)
            return null;

        var properties = ProtocolJsonSerializer.ToJsonElements(context);
        if (properties is null ||
            !properties.TryGetValue("token", out var token) ||
            token.ValueKind != System.Text.Json.JsonValueKind.String)
            return null;

        return token.GetString();
    }

    private bool IsAllowedUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        return _allowedHosts.Any(host =>
            string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;

        host = host.Trim();
        if (host.StartsWith("*.", StringComparison.Ordinal))
            host = host[2..];
        if (Uri.TryCreate(host, UriKind.Absolute, out var uri))
            return uri.Host;
        return host.Split('/', ':')[0];
    }

    private sealed record TeamsGeneratedFileContext(string Token);
}
