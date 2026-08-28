using System.Net;
using System.Net.Http.Headers;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Core.Serialization;

namespace AgentChat.Services;

public interface ITeamsAttachmentService
{
    Task<IReadOnlyList<AgentInputFile>> DownloadAsync(
        ITurnContext turnContext,
        CancellationToken cancellationToken);
}

public sealed class TeamsAttachmentService : ITeamsAttachmentService
{
    private const int MaxRedirects = 5;
    private static readonly string[] DefaultAllowedHosts =
    [
        "botframework.com",
        "smba.trafficmanager.net",
        "teams.microsoft.com",
        "teams.microsoft.us",
        "graph.microsoft.com",
        "sharepoint.com",
        "svc.ms",
        "blob.core.windows.net"
    ];
    private static readonly string[] TokenBearingHosts =
    [
        "botframework.com",
        "smba.trafficmanager.net"
    ];

    private readonly IConnections _connections;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AgentFileService _files;
    private readonly string[] _allowedHosts;
    private readonly int _maxFileCount;
    private readonly long _maxFileBytes;
    private readonly long _maxRequestBytes;

    public TeamsAttachmentService(
        IConnections connections,
        IHttpClientFactory httpClientFactory,
        AgentFileService files,
        IConfiguration configuration)
    {
        _connections = connections;
        _httpClientFactory = httpClientFactory;
        _files = files;
        _maxFileCount = configuration.GetValue("Files:MaxCount", 10);
        _maxFileBytes = configuration.GetValue("Files:MaxFileBytes", 25L * 1024 * 1024);
        _maxRequestBytes = configuration.GetValue("Files:MaxRequestBytes", 50L * 1024 * 1024);
        _allowedHosts = DefaultAllowedHosts
            .Concat(configuration.GetSection("OutboundHostValidator:Hosts").Get<string[]>() ?? [])
            .Select(NormalizeHost)
            .Where(host => host is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
    }

    public async Task<IReadOnlyList<AgentInputFile>> DownloadAsync(
        ITurnContext turnContext,
        CancellationToken cancellationToken)
    {
        if (turnContext.Activity.ChannelId != Channels.Msteams &&
            turnContext.Activity.ChannelId != Channels.M365Copilot)
            return [];

        var attachments = turnContext.Activity.Attachments?
            .Where(attachment =>
                !string.IsNullOrWhiteSpace(attachment.ContentType) &&
                !attachment.ContentType.StartsWith(ContentTypes.Html, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];
        if (attachments.Count == 0)
            return [];
        if (attachments.Count > _maxFileCount)
            throw new InvalidOperationException($"A message can include at most {_maxFileCount} files.");

        var tokenProvider = _connections.GetTokenProvider(turnContext.Identity, turnContext.Activity);
        var accessToken = await tokenProvider.GetAccessTokenAsync(
            turnContext.Identity.GetOutgoingAudience(),
            scopes: null);
        var files = new List<AgentInputFile>(attachments.Count);
        long totalBytes = 0;
        foreach (var attachment in attachments)
        {
            var file = await DownloadAsync(
                attachment,
                accessToken,
                _maxRequestBytes - totalBytes,
                cancellationToken);
            files.Add(file);
            totalBytes += file.Content.ToMemory().Length;
        }

        return _files.Validate(files);
    }

    private async Task<AgentInputFile> DownloadAsync(
        Attachment attachment,
        string accessToken,
        long remainingRequestBytes,
        CancellationToken cancellationToken)
    {
        var fileName = string.IsNullOrWhiteSpace(attachment.Name)
            ? "attachment"
            : Path.GetFileName(attachment.Name);
        var download = GetDownload(attachment);
        if (download is null)
        {
            return new AgentInputFile(
                fileName,
                attachment.ContentType,
                new BinaryData(attachment.Content));
        }

        if (!IsAllowedDownloadUrl(download.Url, download.RequiresAuthorization))
            throw new InvalidOperationException($"Attachment host is not allowed for '{fileName}'.");

        using var client = _httpClientFactory.CreateClient(nameof(TeamsAttachmentService));
        for (var redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, download.Url);
            if (download.RequiresAuthorization)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (IsRedirect(response.StatusCode))
            {
                if (redirectCount == MaxRedirects || response.Headers.Location is null)
                    throw new InvalidOperationException($"Attachment download redirected too many times for '{fileName}'.");

                var redirectedUrl = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(new Uri(download.Url), response.Headers.Location);
                download = new AttachmentDownload(redirectedUrl.ToString(), RequiresAuthorization: false);
                if (!IsAllowedDownloadUrl(download.Url, download.RequiresAuthorization))
                    throw new InvalidOperationException($"Attachment redirect host is not allowed for '{fileName}'.");
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > _maxFileBytes)
                throw FileTooLarge(fileName);
            if (response.Content.Headers.ContentLength > remainingRequestBytes)
                throw RequestTooLarge();

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
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
                    throw FileTooLarge(fileName);
                if (total > remainingRequestBytes)
                    throw RequestTooLarge();
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            return new AgentInputFile(
                fileName,
                response.Content.Headers.ContentType?.MediaType ?? attachment.ContentType,
                BinaryData.FromBytes(output.ToArray()));
        }

        throw new InvalidOperationException($"Attachment download failed for '{fileName}'.");
    }

    private static AttachmentDownload? GetDownload(Attachment attachment)
    {
        var properties = ProtocolJsonSerializer.ToJsonElements(attachment.Content);
        if (properties is not null &&
            properties.TryGetValue("downloadUrl", out var value) &&
            value.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var downloadUrl = value.GetString();
            return string.IsNullOrWhiteSpace(downloadUrl)
                ? null
                : new AttachmentDownload(downloadUrl, RequiresAuthorization: false);
        }

        return string.IsNullOrWhiteSpace(attachment.ContentUrl)
            ? null
            : new AttachmentDownload(attachment.ContentUrl, RequiresAuthorization: true);
    }

    private bool IsAllowedDownloadUrl(string url, bool requiresAuthorization)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        var allowedHosts = requiresAuthorization ? TokenBearingHosts : _allowedHosts;
        return allowedHosts.Any(host =>
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

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private InvalidOperationException FileTooLarge(string fileName)
        => new($"File '{fileName}' exceeds the {_maxFileBytes / 1024 / 1024} MB limit.");

    private InvalidOperationException RequestTooLarge()
        => new($"Attachments exceed the {_maxRequestBytes / 1024 / 1024} MB combined limit.");

    private sealed record AttachmentDownload(string Url, bool RequiresAuthorization);
}
