namespace KONGOR.MasterServer.Controllers.ServerRequester;

public partial class ServerRequesterController
{
    private IActionResult HandleGetSpectatorHeader()
    {
        // Completed Replays Receive Their Own Authenticated Upload Grant; Partial Replay Uploads Have No Configured Destination.
        Dictionary<string, object> response = new ()
        {
            ["target_url"] = string.Empty,
            ["s3_header"] = new Dictionary<string, string>()
        };

        return Ok(PhpSerialization.Serialize(response));
    }

    private async Task<IActionResult> HandleReplayUploadAuthorisation()
    {
        if (ReplayStorage.Settings.Enabled is false)
            return StatusCode(StatusCodes.Status503ServiceUnavailable);

        string hostAccountName = Request.Form["man_masterLogin"].ToString().TrimEnd(':');
        string checksum = Request.Form["md5_checksum"].ToString();

        if (hostAccountName.Length is 0 || Request.Form["file_extension"].ToString() != "honreplay"
            || int.TryParse(Request.Form["match_id"], out int matchID) is false || matchID <= 0
            || int.TryParse(Request.Form["file_size"], out int fileSize) is false || fileSize <= 0 || fileSize > ReplayStorageSettings.MaximumReplayBytes
            || checksum.Length is not 32 || checksum.Any(character => char.IsAsciiHexDigit(character) is false))
            return BadRequest("Invalid Replay Upload Request");

        if (ReplayStorage.ValidateNativeAuthorisation(matchID, "honreplay", Request.Form["pass"].ToString(), Request.Form["hash_key"].ToString()) is false)
            return Unauthorized();

        ReplayAuthorisation? authorisation = await ReplayStorage.GetAuthorisation(matchID, hostAccountName);

        if (authorisation is null || authorisation.MatchID != matchID || authorisation.HostAccountName != hostAccountName
            || authorisation.SourceIPAddress != HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString())
            return Unauthorized();

        Account? hostAccount = await MerrickContext.Accounts.SingleOrDefaultAsync(account => account.Name == hostAccountName);

        if (hostAccount is null || hostAccount.Type is not AccountType.ServerHost
            || (HostEnvironment.IsProduction() && hostAccount.Name == OOTB.Accounts.OPERATOR.Name))
            return Unauthorized();

        MatchStatistics? match = await MerrickContext.MatchStatistics.SingleOrDefaultAsync(match => match.MatchID == matchID);

        if (match is null || match.HostAccountName != hostAccount.Name || match.FileSize != fileSize)
            return Unauthorized();

        if (await ReplayStorage.GetAuthorisation(matchID, hostAccountName, consume: true) is null)
            return Unauthorized();

        string token = await ReplayStorage.CreateGrant(new ReplayUploadGrant(matchID, hostAccount.Name, fileSize, checksum));

        Dictionary<string, object> response = new ()
        {
            ["match_id"] = matchID,
            ["file_extension"] = "honreplay",
            ["TargetURL"] = ReplayStorage.Settings.ReceiverTarget,
            // The Native Manager Sends These Values As Multipart Fields, Despite The Protocol's Name For This Map.
            ["headers"] = new Dictionary<string, string>
            {
                ["upload_token"] = token,
                ["key"] = $"M{matchID}.honreplay"
            }
        };

        return Ok(PhpSerialization.Serialize(response));
    }
}
