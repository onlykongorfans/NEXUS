namespace KONGOR.MasterServer.Helpers.Stats;

/// <summary>
///     Resolves recorded mastery progression while keeping matches recorded before progression snapshots usable.
/// </summary>
public static class MasteryProgressionHelper
{
    public static MasteryProgression? Resolve(Mastery mastery, MatchParticipantStatistics participant, AccountStatisticsType statisticsType, MasteryBoostContext? boost)
    {
        if (participant.MasteryProgression is MasteryProgression recorded)
        {
            if (boost is null)
                return recorded;

            // Older Binaries Can Record A Durable Boost Without Updating An Existing Progression Snapshot
            return new MasteryProgression
            {
                ExperienceBeforeMatch = recorded.ExperienceBeforeMatch,
                MatchExperience = recorded.MatchExperience,
                HeroesAtMaximumMasteryCount = recorded.HeroesAtMaximumMasteryCount,
                BonusExperience = recorded.BonusExperience,
                BoostExperience = boost.IsSuperBoost ? 0 : boost.Experience,
                SuperBoostExperience = boost.IsSuperBoost ? boost.Experience : 0
            };
        }

        int matchExperience = Mastery.CalculateMatchExperience(statisticsType, participant.HeroLevel);

        if (matchExperience <= 0)
            return null;

        int heroesAtMaximumMasteryCount = mastery.HeroesAtMaximumMasteryCount();
        int bonusExperience = CalculateLegacyBonusExperience(statisticsType, heroesAtMaximumMasteryCount);

        // Legacy Matches Have No Historical Starting Value; Preserve Their Previous Reconstruction And Bonus Formula Without Rewriting Stored Experience
        return new MasteryProgression
        {
            ExperienceBeforeMatch = Math.Max(0, mastery.GetHeroExperienceByHeroIdentifier(participant.HeroIdentifier) - matchExperience - bonusExperience - (boost?.Experience ?? 0)),
            MatchExperience = matchExperience,
            HeroesAtMaximumMasteryCount = heroesAtMaximumMasteryCount,
            BonusExperience = bonusExperience,
            BoostExperience = boost is { IsSuperBoost: false } ? boost.Experience : 0,
            SuperBoostExperience = boost is { IsSuperBoost: true } ? boost.Experience : 0
        };
    }

    private static int CalculateLegacyBonusExperience(AccountStatisticsType statisticsType, int heroesAtMaximumMasteryCount)
    {
        float percentage = Convert.ToSingle(heroesAtMaximumMasteryCount) / Heroes.TotalHeroCount * 100f;

        int experience = percentage switch
        {
            < 5  => 0,
            < 10 => 10,
            < 20 => 20,
            < 30 => 30,
            < 40 => 40,
            < 50 => 50,
            < 60 => 60,
            < 70 => 70,
            < 80 => 80,
            < 90 => 90,
            _    => 100
        };

        return statisticsType switch
        {
            AccountStatisticsType.Matchmaking       => experience,
            AccountStatisticsType.MatchmakingCasual => experience / 2,
            AccountStatisticsType.MidWars           => experience / 2,
            _                                       => 0
        };
    }
}
