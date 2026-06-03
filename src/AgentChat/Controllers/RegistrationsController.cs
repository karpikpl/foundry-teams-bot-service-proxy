using AgentChat.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentChat.Controllers;

/// <summary>
/// CRUD for bot ↔ agent registrations. Each generated Teams manifest needs a
/// botId so Teams chats route to a specific Bot Service registration; with
/// URL-routed multi-agent, that mapping is many-to-one (per agent).
///
/// Routes are always (foundryHost, project, agentName) — there is no
/// "configured default project" shortcut. Operators must explicitly register
/// each agent they want a manifest for.
/// </summary>
[ApiController]
[Route("admin/registrations")]
public class RegistrationsController : ControllerBase
{
    private readonly BotRegistrationStore _store;
    private readonly ILogger<RegistrationsController> _logger;

    public RegistrationsController(BotRegistrationStore store, ILogger<RegistrationsController> logger)
    {
        _store  = store;
        _logger = logger;
    }

    public sealed record RegistrationDto(
        string FoundryHost,
        string Project,
        string AgentName,
        string BotId,
        string? DisplayName,
        DateTime CreatedUtc,
        DateTime UpdatedUtc);

    public sealed record PutRegistrationRequest(string BotId, string? DisplayName);

    private static RegistrationDto ToDto(BotRegistration r)
        => new(r.FoundryHost, r.Project, r.AgentName, r.BotId, r.DisplayName, r.CreatedUtc, r.UpdatedUtc);

    [HttpGet("")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var all = await _store.ListAsync(ct);
        return Ok(all.Select(ToDto));
    }

    [HttpGet("{foundryHost}/{project}/{agentName}")]
    public async Task<IActionResult> Get(string foundryHost, string project, string agentName, CancellationToken ct)
    {
        var reg = await _store.GetAsync(foundryHost, project, agentName, ct);
        return reg is null ? NotFound() : Ok(ToDto(reg));
    }

    [HttpPut("{foundryHost}/{project}/{agentName}")]
    public async Task<IActionResult> Put(
        string foundryHost, string project, string agentName,
        [FromBody] PutRegistrationRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body?.BotId))
            return BadRequest(new { error = "botId is required" });

        // botId must be a GUID — Bot Service MSA app ids are GUIDs and we'd
        // rather catch typos here than at manifest sideload time.
        if (!Guid.TryParse(body.BotId, out _))
            return BadRequest(new { error = "botId must be a valid GUID" });

        var existing = await _store.GetAsync(foundryHost, project, agentName, ct);
        var reg = new BotRegistration
        {
            FoundryHost = foundryHost,
            Project     = project,
            AgentName   = agentName,
            BotId       = body.BotId,
            DisplayName = body.DisplayName,
            CreatedUtc  = existing?.CreatedUtc ?? DateTime.UtcNow,
            UpdatedUtc  = DateTime.UtcNow
        };
        await _store.PutAsync(reg, ct);
        return Ok(ToDto(reg));
    }

    [HttpDelete("{foundryHost}/{project}/{agentName}")]
    public async Task<IActionResult> Delete(string foundryHost, string project, string agentName, CancellationToken ct)
    {
        await _store.DeleteAsync(foundryHost, project, agentName, ct);
        return NoContent();
    }
}
