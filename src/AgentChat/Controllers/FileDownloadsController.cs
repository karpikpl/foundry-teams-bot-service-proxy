using AgentChat.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentChat.Controllers;

[ApiController]
[Route("api/files")]
public sealed class FileDownloadsController(AgentFileService files) : ControllerBase
{
    [HttpGet("{token}/{fileName?}")]
    public IActionResult Download(string token, string? fileName = null)
    {
        if (!files.TryGetDownload(token, out var download))
            return NotFound();

        Response.Headers.CacheControl = "private, no-store";
        return File(download.Content, download.ContentType, download.FileName);
    }
}
