namespace KONGOR.MasterServer.Helpers.Stats;

/// <summary>
///     Shares boost eligibility between the match statistics response and the boost endpoint, retaining the production match and time-window rules.
/// </summary>
public static class MasteryBoostEligibility
{
    public static async Task<MasteryBoostRejection?> GetRejection(MerrickContext databaseContext, Mastery mastery, MatchParticipantStatistics participant,
        MatchStatistics match, MasteryProgression? progression, MasteryBoostContext? boost)
    {
        // Every Match Advances The Latest-Match Boundary, Including Modes That Do Not Award Mastery Experience
        int mostRecentMatchID = await databaseContext.MatchParticipantStatistics
            .Where(statistics => statistics.AccountID == participant.AccountID)
            .Join(databaseContext.MatchStatistics, statistics => statistics.MatchID, statistics => statistics.MatchID,
                (statistics, recordedMatch) => new { statistics.ID, statistics.MatchID, recordedMatch.TimestampRecorded })
            .OrderByDescending(record => record.TimestampRecorded)
            .ThenByDescending(record => record.ID)
            .Select(record => record.MatchID)
            .Take(1)
            .SingleOrDefaultAsync();

        if (participant.MatchID != mostRecentMatchID)
            return new MasteryBoostRejection(5, "Hero Mastery Boosts May Only Be Applied To Your Most Recent Match");

        if (participant.MasteryBoostExperience is not null || boost is not null || progression is { BoostExperience: > 0 } or { SuperBoostExperience: > 0 })
            return new MasteryBoostRejection(4, "A Hero Mastery Boost Has Already Been Applied To This Match");

        if (match.TimestampRecorded < DateTimeOffset.UtcNow - MasteryBoost.ApplicationWindow)
            return new MasteryBoostRejection(5, $"Hero Mastery Boosts May Only Be Applied Within {MasteryBoost.ApplicationWindow.Days} Days Of The Match Being Played");

        if (progression is null || progression.MatchExperience <= 0)
            return new MasteryBoostRejection(6, $"No Mastery Data Exists For Match ID {participant.MatchID}");

        if (mastery.GetHeroLevelByHeroIdentifier(participant.HeroIdentifier) >= Mastery.MaximumMasteryLevel)
            return new MasteryBoostRejection(1, "Hero Mastery Boosts Cannot Be Applied To A Hero At The Maximum Mastery Level");

        return null;
    }
}

public record MasteryBoostRejection(int ErrorCode, string ErrorMessage);
