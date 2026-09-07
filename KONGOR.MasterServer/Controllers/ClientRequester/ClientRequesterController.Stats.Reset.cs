namespace KONGOR.MasterServer.Controllers.ClientRequester;

public partial class ClientRequesterController
{
    private const int StatResetProductID = 163;
    private const double InitialSkillRating = 1500.0;

    /// <summary>
    ///     Redeems a purchased Stat Reset for the statistics selected in the legacy game-client form.
    ///     The client submits an MD5 password digest, which can be checked against the user's existing
    ///     SRP password record without receiving or storing the plaintext password.
    /// </summary>
    private async Task<IActionResult> ResetStatistics()
    {
        StatResetSelection selection = ReadStatResetSelection();

        if (selection.HasAnySelection.Equals(false))
            return StatResetFailure("no options provided");

        string cookie = Request.Form["cookie"].ToString();
        string? authenticatedAccountName = await DistributedCache.GetAccountNameForSessionCookie(cookie);

        if (authenticatedAccountName is null)
            return StatResetFailure("internal error");

        RedisKey resetLockKey = $"STAT-RESET-LOCK:{authenticatedAccountName}";
        RedisValue resetLockToken = Guid.CreateVersion7().ToString();
        TimeSpan resetLockExpiration = TimeSpan.FromSeconds(30);

        while (await DistributedCache.LockTakeAsync(resetLockKey, resetLockToken, resetLockExpiration) is false)
            await Task.Delay(TimeSpan.FromMilliseconds(25), HttpContext.RequestAborted);

        try
        {
            return await ResetStatistics(authenticatedAccountName, selection);
        }

        finally
        {
            await DistributedCache.LockReleaseAsync(resetLockKey, resetLockToken);
        }
    }

    private async Task<IActionResult> ResetStatistics(string authenticatedAccountName, StatResetSelection selection)
    {
        if (int.TryParse(Request.Form["account_id"], out int requestedAccountID).Equals(false))
            return StatResetFailure("internal error");

        Account? account = await MerrickContext.Accounts
            .Include(candidate => candidate.User)
            .SingleOrDefaultAsync(candidate => candidate.Name.Equals(authenticatedAccountName));

        if (account is null)
        {
            Logger.LogError("[BUG] Session Cookie Resolved To Non-Existent Account {AccountName} During A Stat Reset", authenticatedAccountName);

            return StatResetFailure("internal error");
        }

        return await UserInventoryTransaction.ExecuteForAccount(MerrickContext, account.ID,
            lockedAccount => ResetStatistics(lockedAccount, requestedAccountID, selection), HttpContext.RequestAborted);
    }

    private async Task<IActionResult> ResetStatistics(Account account, int requestedAccountID, StatResetSelection selection)
    {
        if (account.ID != requestedAccountID)
        {
            Logger.LogWarning("Stat Reset Account ID Mismatch For {AccountName} (Expected {AccountID}, Received {RequestedAccountID})",
                account.Name, account.ID, requestedAccountID);

            return StatResetFailure("internal error");
        }

        string submittedPasswordHash = Request.Form["password"].ToString();
        string computedPasswordHash = SRPAuthenticationHandlers.ComputeSRPPasswordHash(submittedPasswordHash, account.User.SRPPasswordSalt, passwordIsHashed: true);

        if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(computedPasswordHash), Encoding.UTF8.GetBytes(account.User.SRPPasswordHash)).Equals(false))
            return StatResetFailure("wrong password");

        StoreItem? statResetProduct = StoreItems.GetByID(StatResetProductID);

        if (statResetProduct is null)
        {
            Logger.LogError("[BUG] Stat Reset Store Product {ProductID} Was Not Found", StatResetProductID);

            return StatResetFailure("internal error");
        }

        if (account.User.OwnedStoreItems.Contains(statResetProduct.PrefixedCode).Equals(false))
            return StatResetFailure("not enough stats reset");

        Dictionary<AccountStatisticsType, AccountStatistics> statistics = await MerrickContext.AccountStatistics
            .Where(candidate => candidate.AccountID == account.ID)
            .ToDictionaryAsync(candidate => candidate.Type);

        AccountStatisticsType[] requiredStatisticsTypes = selection.GetRequiredStatisticsTypes();
        AccountStatisticsType[] missingStatisticsTypes = requiredStatisticsTypes.Where(type => statistics.ContainsKey(type).Equals(false)).ToArray();

        if (missingStatisticsTypes.Length > 0)
        {
            Logger.LogError("[BUG] Account {AccountName} (ID: {AccountID}) Is Missing Statistics Rows {StatisticsTypes} During A Stat Reset",
                account.Name, account.ID, missingStatisticsTypes);

            return StatResetFailure("internal error");
        }

        ApplyStatReset(statistics, selection);

        // Saving the selected statistics and consuming the product together keeps the redemption atomic.
        account.User.OwnedStoreItems.Remove(statResetProduct.PrefixedCode);

        await MerrickContext.SaveChangesAsync();

        Logger.LogInformation("Account {AccountName} (ID: {AccountID}) Redeemed A Stat Reset With Selection {Selection}",
            account.Name, account.ID, selection);

        return Ok(JsonSerializer.Serialize(new { success = true }));
    }

    private StatResetSelection ReadStatResetSelection()
        => new (
            PublicStatistics: IsSelected("public"),
            PublicSkillRating: IsSelected("psr"),
            NormalStatistics: IsSelected("normalmmstats"),
            NormalRating: IsSelected("smr"),
            CasualStatistics: IsSelected("casualmmstats"),
            CasualRating: IsSelected("casualmmr"),
            MidWarsStatistics: IsSelected("midwars"),
            MidWarsRating: IsSelected("mmmidwars"),
            ConNormalStatisticsAndRating: IsSelected("connormalstats"),
            ConCasualStatisticsAndRating: IsSelected("concasualstats"));

    private bool IsSelected(string formParameter)
        => Request.Form[formParameter].ToString() is "1";

    private static void ApplyStatReset(IReadOnlyDictionary<AccountStatisticsType, AccountStatistics> statistics, StatResetSelection selection)
    {
        if (selection.PublicStatistics || selection.PublicSkillRating)
        {
            AccountStatistics publicStatistics = statistics[AccountStatisticsType.Public];

            if (selection.PublicStatistics)
                ResetStatisticsExceptRating(publicStatistics);

            if (selection.PublicSkillRating)
                ResetRating(publicStatistics);
        }

        if (selection.NormalStatistics || selection.NormalRating || selection.ConNormalStatisticsAndRating)
        {
            AccountStatistics normalStatistics = statistics[AccountStatisticsType.Matchmaking];

            if (selection.NormalStatistics || selection.ConNormalStatisticsAndRating)
                ResetStatisticsExceptRating(normalStatistics);

            if (selection.NormalRating || selection.ConNormalStatisticsAndRating)
                ResetRating(normalStatistics);
        }

        if (selection.CasualStatistics || selection.CasualRating || selection.ConCasualStatisticsAndRating)
        {
            AccountStatistics casualStatistics = statistics[AccountStatisticsType.MatchmakingCasual];

            if (selection.CasualStatistics || selection.ConCasualStatisticsAndRating)
                ResetStatisticsExceptRating(casualStatistics);

            if (selection.CasualRating || selection.ConCasualStatisticsAndRating)
                ResetRating(casualStatistics);
        }

        if (selection.MidWarsStatistics || selection.MidWarsRating)
        {
            AccountStatistics midWarsStatistics = statistics[AccountStatisticsType.MidWars];

            if (selection.MidWarsStatistics)
                ResetStatisticsExceptRating(midWarsStatistics);

            if (selection.MidWarsRating)
                ResetRating(midWarsStatistics);
        }
    }

    private static void ResetStatisticsExceptRating(AccountStatistics statistics)
    {
        statistics.MatchesPlayed = 0;
        statistics.MatchesWon = 0;
        statistics.MatchesLost = 0;
        statistics.MatchesDisconnected = 0;
        statistics.MatchesConceded = 0;
        statistics.MatchesKicked = 0;
        statistics.HeroKills = 0;
        statistics.HeroAssists = 0;
        statistics.HeroDeaths = 0;
        statistics.WardsPlaced = 0;
        statistics.Smackdowns = 0;
        statistics.HeroStatistics.Heroes.Clear();

        AwardStatisticsSummary awards = statistics.AwardStatistics;
        awards.MVPAwards = 0;
        awards.AnnihilationAwards = 0;
        awards.QuadKillAwards = 0;
        awards.LongestKillStreakAwards = 0;
        awards.SmackdownAwards = 0;
        awards.MostKillsAwards = 0;
        awards.MostAssistsAwards = 0;
        awards.LeastDeathsAwards = 0;
        awards.MostBuildingDamageAwards = 0;
        awards.MostWardsDestroyedAwards = 0;
        awards.MostHeroDamageDealtAwards = 0;
        awards.HighestCreepScoreAwards = 0;
    }

    private static void ResetRating(AccountStatistics statistics)
    {
        statistics.SkillRating = InitialSkillRating;

        if (statistics.Type is AccountStatisticsType.Matchmaking or AccountStatisticsType.MatchmakingCasual)
            statistics.PlacementMatchesData = string.Empty;
    }

    private static IActionResult StatResetFailure(string error)
        => new OkObjectResult(JsonSerializer.Serialize(new { success = false, errors = error }));

    private sealed record StatResetSelection(
        bool PublicStatistics,
        bool PublicSkillRating,
        bool NormalStatistics,
        bool NormalRating,
        bool CasualStatistics,
        bool CasualRating,
        bool MidWarsStatistics,
        bool MidWarsRating,
        bool ConNormalStatisticsAndRating,
        bool ConCasualStatisticsAndRating)
    {
        public bool HasAnySelection => PublicStatistics
            || PublicSkillRating
            || NormalStatistics
            || NormalRating
            || CasualStatistics
            || CasualRating
            || MidWarsStatistics
            || MidWarsRating
            || ConNormalStatisticsAndRating
            || ConCasualStatisticsAndRating;

        public AccountStatisticsType[] GetRequiredStatisticsTypes()
        {
            List<AccountStatisticsType> types = [];

            if (PublicStatistics || PublicSkillRating)
                types.Add(AccountStatisticsType.Public);

            if (NormalStatistics || NormalRating || ConNormalStatisticsAndRating)
                types.Add(AccountStatisticsType.Matchmaking);

            if (CasualStatistics || CasualRating || ConCasualStatisticsAndRating)
                types.Add(AccountStatisticsType.MatchmakingCasual);

            if (MidWarsStatistics || MidWarsRating)
                types.Add(AccountStatisticsType.MidWars);

            return [.. types];
        }
    }
}
