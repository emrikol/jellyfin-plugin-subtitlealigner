using System.Net.Mime;
using Jellyfin.Plugin.SubtitleAligner.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleAligner.Controllers;

/// <summary>Endpoints for administration and per-title subtitle alignment.</summary>
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Authorize]
[Route("SubtitleAligner")]
public sealed class SubtitleAlignerController : ControllerBase
{
    private readonly SubtitleValidationService _service;
    private readonly ITaskManager _taskManager;
    private readonly WhisperModelManager _modelManager;
    private readonly HostAssessmentService _hostAssessment;
    private readonly WhisperServer _whisperServer;
    private readonly TranscriptionCache _transcriptionCache;
    private readonly IAuthorizationService _authorizationService;

    /// <summary>Initializes a new instance of the <see cref="SubtitleAlignerController"/> class.</summary>
    public SubtitleAlignerController(
        SubtitleValidationService service,
        ITaskManager taskManager,
        WhisperModelManager modelManager,
        HostAssessmentService hostAssessment,
        WhisperServer whisperServer,
        TranscriptionCache transcriptionCache,
        IAuthorizationService authorizationService)
    {
        _service = service;
        _taskManager = taskManager;
        _modelManager = modelManager;
        _hostAssessment = hostAssessment;
        _whisperServer = whisperServer;
        _transcriptionCache = transcriptionCache;
        _authorizationService = authorizationService;
    }

    /// <summary>Serves the small, same-origin subtitle-editor integration script.</summary>
    [HttpGet("client.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public IActionResult ClientScript()
    {
        Stream? stream = typeof(Plugin).Assembly.GetManifestResourceStream(
            "Jellyfin.Plugin.SubtitleAligner.Web.subtitlealigner-client.js");
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache";
        return File(stream, "application/javascript");
    }

    /// <summary>Gets current activity and recent results.</summary>
    [HttpGet("Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType<ValidatorStatus>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ValidatorStatus>> Status(CancellationToken cancellationToken)
    {
        return Ok(await _service.GetStatusAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Queues an immediate pass that ignores the configured run window.</summary>
    [HttpPost("Run")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult Run()
    {
        _service.RequestManualRun();
        _taskManager.QueueIfNotRunning<SubtitleValidationTask>();
        return Accepted();
    }

    /// <summary>Finds videos that have supported text subtitle tracks.</summary>
    [HttpGet("Items")]
    [ProducesResponseType<IReadOnlyList<VideoItemOption>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<VideoItemOption>>> Items([FromQuery] string search = "")
    {
        if (!await CanManageSubtitlesAsync().ConfigureAwait(false))
        {
            return Forbid();
        }

        return Ok(_service.SearchItems(search));
    }

    /// <summary>Queues an immediate, forced recheck and correction attempt for one video.</summary>
    [HttpPost("Sync/{itemId:guid}")]
    [ProducesResponseType<ItemSyncAccepted>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ItemSyncAccepted>> Sync(
        Guid itemId,
        [FromQuery] bool retranscribe = false)
    {
        if (!await CanManageSubtitlesAsync().ConfigureAwait(false))
        {
            return Forbid();
        }

        ItemSyncAccepted accepted = _service.RequestItemRun(
            itemId,
            SubtitleAlignmentMode.Quick,
            retranscribe);
        _taskManager.QueueIfNotRunning<SubtitleValidationTask>();
        return Accepted(accepted);
    }

    /// <summary>Queues a complete local-timing alignment pass for one video.</summary>
    [HttpPost("Align/{itemId:guid}")]
    [ProducesResponseType<ItemSyncAccepted>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ItemSyncAccepted>> Align(
        Guid itemId,
        [FromQuery] bool retranscribe = false)
    {
        if (!await CanManageSubtitlesAsync().ConfigureAwait(false))
        {
            return Forbid();
        }

        ItemSyncAccepted accepted = _service.RequestItemRun(
            itemId,
            SubtitleAlignmentMode.Full,
            retranscribe);
        _taskManager.QueueIfNotRunning<SubtitleValidationTask>();
        return Accepted(accepted);
    }

    /// <summary>Queues a title identified by the exact media file name displayed in the subtitle editor.</summary>
    [HttpPost("SyncByFile")]
    [ProducesResponseType<ItemSyncAccepted>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ItemSyncAccepted>> SyncByFile([FromBody] SyncByFileRequest request)
    {
        if (!await CanManageSubtitlesAsync().ConfigureAwait(false))
        {
            return Forbid();
        }

        Guid? itemId = _service.FindUniqueItemByFileName(request.FileName, request.ItemId);
        if (!itemId.HasValue)
        {
            return NotFound(new { Error = "The subtitle editor's video could not be identified uniquely." });
        }

        ItemSyncAccepted accepted = _service.RequestItemRun(itemId.Value);
        _taskManager.QueueIfNotRunning<SubtitleValidationTask>();
        return Accepted(accepted);
    }

    /// <summary>Queues a complete alignment pass for the exact media file shown in the subtitle editor.</summary>
    [HttpPost("AlignByFile")]
    [ProducesResponseType<ItemSyncAccepted>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ItemSyncAccepted>> AlignByFile([FromBody] SyncByFileRequest request)
    {
        if (!await CanManageSubtitlesAsync().ConfigureAwait(false))
        {
            return Forbid();
        }

        Guid? itemId = _service.FindUniqueItemByFileName(request.FileName, request.ItemId);
        if (!itemId.HasValue)
        {
            return NotFound(new { Error = "The subtitle editor's video could not be identified uniquely." });
        }

        ItemSyncAccepted accepted = _service.RequestItemRun(itemId.Value, SubtitleAlignmentMode.Full);
        _taskManager.QueueIfNotRunning<SubtitleValidationTask>();
        return Accepted(accepted);
    }

    /// <summary>Gets live progress and results for one title check.</summary>
    [HttpGet("SyncStatus/{runId:guid}")]
    [ProducesResponseType<ItemSyncStatus>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ItemSyncStatus>> SyncStatus(Guid runId)
    {
        if (!await CanManageSubtitlesAsync().ConfigureAwait(false))
        {
            return Forbid();
        }

        ItemSyncStatus? status = _service.GetItemRunStatus(runId);
        return status is null ? NotFound() : Ok(status);
    }

    /// <summary>Gets the latest stored findings and any active run for one title.</summary>
    [HttpGet("Results/{itemId:guid}")]
    [ProducesResponseType<ItemResultSnapshot>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ItemResultSnapshot>> Results(Guid itemId, CancellationToken cancellationToken)
    {
        if (!await CanManageSubtitlesAsync().ConfigureAwait(false))
        {
            return Forbid();
        }

        return Ok(await _service.GetItemResultsAsync(itemId, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Gets stored findings for the exact media file shown in the subtitle editor.</summary>
    [HttpGet("ResultsByFile")]
    [ProducesResponseType<ItemResultSnapshot>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ItemResultSnapshot>> ResultsByFile(
        [FromQuery] string fileName,
        [FromQuery] Guid? itemId,
        CancellationToken cancellationToken)
    {
        if (!await CanManageSubtitlesAsync().ConfigureAwait(false))
        {
            return Forbid();
        }

        Guid? resolvedItemId = _service.FindUniqueItemByFileName(fileName, itemId);
        if (!resolvedItemId.HasValue)
        {
            return NotFound(new { Error = "The subtitle editor's video could not be identified uniquely." });
        }

        return Ok(await _service.GetItemResultsAsync(resolvedItemId.Value, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Gets the plugin-managed default model state.</summary>
    [HttpGet("Model")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType<WhisperModelStatus>(StatusCodes.Status200OK)]
    public async Task<ActionResult<WhisperModelStatus>> Model(CancellationToken cancellationToken)
    {
        return Ok(await _modelManager.GetStatusAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Downloads the pinned default model from its upstream repository.</summary>
    [HttpPost("Model/Download")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType<WhisperModelStatus>(StatusCodes.Status200OK)]
    public async Task<ActionResult<WhisperModelStatus>> DownloadModel(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _modelManager.DownloadAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or InvalidOperationException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { Error = exception.Message });
        }
    }

    /// <summary>Gets host compatibility advice and current or recent benchmark state.</summary>
    [HttpGet("Model/Assessment")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType<HostAssessmentStatus>(StatusCodes.Status200OK)]
    public ActionResult<HostAssessmentStatus> ModelAssessment()
    {
        return Ok(_hostAssessment.GetStatus());
    }

    /// <summary>Starts an optional background throughput benchmark for one compatible model.</summary>
    [HttpPost("Model/Assessment")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult AssessModel([FromBody] ModelBenchmarkRequest request)
    {
        return _hostAssessment.TryStart(request.ModelName, out string message)
            ? Accepted(new { Message = message })
            : Conflict(new { Error = message });
    }

    /// <summary>Checks a trusted external whisper-server without saving the address.</summary>
    [HttpPost("Whisper/Test")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType<WhisperServerTestResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<WhisperServerTestResult>> TestWhisperServer(
        [FromBody] WhisperServerTestRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _whisperServer.TestExternalAsync(request.Url, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Gets current transcription-cache usage and retention settings.</summary>
    [HttpGet("Cache")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType<TranscriptionCacheStatus>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TranscriptionCacheStatus>> Cache(CancellationToken cancellationToken)
    {
        return Ok(await _transcriptionCache
            .GetStatusAsync(Plugin.Instance.Configuration, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>Deletes all plugin-owned cached speech responses.</summary>
    [HttpDelete("Cache")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType<TranscriptionCacheStatus>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TranscriptionCacheStatus>> ClearCache(CancellationToken cancellationToken)
    {
        await _transcriptionCache.ClearAsync(cancellationToken).ConfigureAwait(false);
        return Ok(await _transcriptionCache
            .GetStatusAsync(Plugin.Instance.Configuration, cancellationToken)
            .ConfigureAwait(false));
    }

    private async Task<bool> CanManageSubtitlesAsync()
    {
        AuthorizationResult administrator = await _authorizationService
            .AuthorizeAsync(User, Policies.RequiresElevation)
            .ConfigureAwait(false);
        if (administrator.Succeeded)
        {
            return true;
        }

        AuthorizationResult subtitleManager = await _authorizationService
            .AuthorizeAsync(User, Policies.SubtitleManagement)
            .ConfigureAwait(false);
        return subtitleManager.Succeeded;
    }

}
