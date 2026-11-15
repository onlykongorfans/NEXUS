namespace KONGOR.MasterServer.Controllers.Replays;

[ApiController]
[Route("replays/upload")]
public sealed class ReplayUploadController(ReplayStorageService replayStorage, IDatabase distributedCache, MerrickContext databaseContext, ILogger<ReplayUploadController> logger) : ControllerBase
{
    [HttpPost]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting("ReplayUploads")]
    [RequestSizeLimit(ReplayStorageSettings.MaximumReplayBytes + 65536)]
    [RequestFormLimits(MultipartBodyLengthLimit = ReplayStorageSettings.MaximumReplayBytes)]
    public async Task<IActionResult> Upload(CancellationToken cancellationToken)
    {
        if (replayStorage.Settings.Enabled is false)
            return StatusCode(StatusCodes.Status503ServiceUnavailable);

        IFormCollection form = await Request.ReadFormAsync(cancellationToken);
        string token = form["upload_token"].ToString();
        ReplayUploadGrant? grant = await replayStorage.GetGrant(token);

        if (grant is null)
            return Unauthorized();

        string expectedFileName = $"M{grant.MatchID}.honreplay";
        IFormFile? file = form.Files.Count is 1 ? form.Files.Single() : null;

        if (form["key"].ToString() != expectedFileName || file is null || file.Name != "file" || file.Length != grant.FileSize)
            return BadRequest("Replay File Does Not Match The Upload Grant");

        MatchStatistics? match = await databaseContext.MatchStatistics.SingleOrDefaultAsync(match => match.MatchID == grant.MatchID, cancellationToken);

        if (match is null || match.HostAccountName != grant.HostAccountName)
            return Unauthorized();

        RedisKey lockKey = $"REPLAY:UPLOAD-LOCK:{grant.MatchID}";
        RedisValue lockToken = Guid.NewGuid().ToString();

        if (await distributedCache.LockTakeAsync(lockKey, lockToken, TimeSpan.FromMinutes(10)) is false)
            return Conflict("Replay Upload Already In Progress");

        try
        {
            if (await replayStorage.GetGrant(token) is null)
                return Unauthorized();

            await using (Stream verification = file.OpenReadStream())
            {
                string checksum = Convert.ToHexString(await MD5.HashDataAsync(verification, cancellationToken));

                if (checksum.Equals(grant.MD5Checksum, StringComparison.OrdinalIgnoreCase) is false)
                    return BadRequest("Replay Checksum Does Not Match The Upload Grant");
            }

            await using Stream replay = file.OpenReadStream();

            if ((await replayStorage.GetAvailableSize(grant.MatchID, cancellationToken, refresh: true)).HasValue)
                return Conflict("Replay Already Stored");

            if (await replayStorage.Upload(grant.MatchID, replay, grant.FileSize, cancellationToken) is false)
                return StatusCode(StatusCodes.Status502BadGateway);

            match.FileSize = grant.FileSize;
            await databaseContext.SaveChangesAsync(cancellationToken);
            await distributedCache.KeyDeleteAsync(ReplayStorageService.GrantKey(token));

            logger.LogInformation("Stored Replay For Match {MatchID} From Host {HostAccountName}, {FileSize} Bytes", grant.MatchID, grant.HostAccountName, grant.FileSize);
            return NoContent();
        }
        finally
        {
            await distributedCache.LockReleaseAsync(lockKey, lockToken);
        }
    }
}
