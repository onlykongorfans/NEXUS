namespace MERRICK.DatabaseContext.Handlers;

public static class SeedDataHandlers
{
    public static async Task SeedUsers(MerrickContext context, CancellationToken cancellationToken, ILogger logger)
    {
        if (await context.Users.AnyAsync(cancellationToken))
        {
            logger.LogDebug("Skipped Seeding Users: Users Have Already Been Seeded");

            return;
        }

        if (await context.Roles.NoneAsync(cancellationToken))
        {
            logger.LogDebug("Skipped Seeding Users: Roles Have Not Yet Been Seeded");

            return;
        }

        Role roleAdministrator = await context.Roles.SingleAsync(role => role.Name.Equals(UserRoles.Administrator), cancellationToken: cancellationToken);

        User userAdministrator = new ()
        {
            EmailAddress = "project.kongor@proton.me",
            Role = roleAdministrator,
            SRPPasswordSalt = "861c37ec6d049d92cc1c67d195b414f26b572a56358272af3e9c06fcd9bfa053",
            SRPPasswordHash = "fe6f16b0ecb80f6b2bc95d68420fd13afef0c895172a81819870660208ac221a",
            PBKDF2PasswordHash = "AQAAAAIAAYagAAAAEMUkpLAr01NjkKRPaXCyTa17nlOdPKJucn5QYur+wQBTDKCpgsAcREenK+pGJPBCRw==",
            GoldCoins = 5_555_555,
            SilverCoins = 555_555_555,
            PlinkoTickets = 5_555_555,
            TotalLevel = 666,
            TotalExperience = 222_11_666,

            // TODO: Add All Upgrades (Including Missing Ones From PK Version Control); Maybe Scrape The Resources Again

            OwnedStoreItems = [ "ai.custom_icon:1", "av.Flamboyant", "c.cat_courier", "cc.frostburnlogo", "cr.Punk Creep", "cs.frostburnlogo", "m.Super-Taunt", "sc.paragon_circle_upgrade", "t.Dumpster_Taunt", "te.Punk TP", "w.8bit_ward", "ma.Mastery Boost * 5555", "ma.Super Mastery Boost * 555" ]
        };

        await context.Users.AddAsync(userAdministrator, cancellationToken);

        Role roleGuest = await context.Roles.SingleAsync(role => role.Name.Equals(UserRoles.User), cancellationToken: cancellationToken);

        User userGuest = new ()
        {
            EmailAddress = OOTB.Accounts.GUEST.EmailAddress,
            Role = roleGuest,
            SRPPasswordSalt = "81c278a3ed03cf4549e787feac5ffe1d051029e15159b1cf0b087b4f8a85cb69",
            SRPPasswordHash = "bfad34f4dc65064dcae6e2064e12d63b299909b6c0cdb33306a91f26bb624e35",
            PBKDF2PasswordHash = "AQAAAAIAAYagAAAAECx4DCy3qYARlevpaVUJXy28QyvXDcQiJZ1KOSx547WDLBV0pdx2MX5m+Pe3xhtQAg==",
            GoldCoins = 5_555_555,
            SilverCoins = 555_555_555,
            PlinkoTickets = 5_555_555,
            TotalLevel = 0,
            TotalExperience = 0,

            // TODO: Add All Upgrades (Including Missing Ones From PK Version Control); Maybe Scrape The Resources Again

            OwnedStoreItems = ["ai.custom_icon:1", "av.Flamboyant", "c.cat_courier", "cc.frostburnlogo", "cr.Punk Creep", "cs.frostburnlogo", "m.Super-Taunt", "sc.paragon_circle_upgrade", "t.Dumpster_Taunt", "te.Punk TP", "w.8bit_ward", "ma.Mastery Boost * 5555", "ma.Super Mastery Boost * 555"]
        };

        await context.Users.AddAsync(userGuest, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(@"Seeded Administrator And Guest Users With Email Addresses ""{AdministratorEmailAddress}"" And ""{GuestEmailAddress}""",
            userAdministrator.EmailAddress, userGuest.EmailAddress);
    }

    public static async Task SeedClans(MerrickContext context, CancellationToken cancellationToken, ILogger logger)
    {
        if (await context.Clans.AnyAsync(cancellationToken))
        {
            logger.LogDebug("Skipped Seeding Clans: Clans Have Already Been Seeded");

            return;
        }

        IEnumerable<Clan> clans =
        [
            new Clan { Name = "KONGOR", Tag = "K" },
            new Clan { Name = "Project KONGOR", Tag = "PK" },
            new Clan { Name = "Project KONGOR Open-Source", Tag = "PKOS" },
            new Clan { Name = "Heroes Of Newerth", Tag = "HON" }
        ];

        await context.Clans.AddRangeAsync(clans, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(@"Seeded {ClanCount} Clans: {ClanNames}",
            clans.Count(), string.Join(", ", clans.Select(clan => clan.Name)));
    }

    public static async Task SeedAccounts(MerrickContext context, CancellationToken cancellationToken, ILogger logger, bool seedBuiltInGuestAccounts)
    {
        if (await context.Accounts.AnyAsync(cancellationToken))
        {
            logger.LogDebug("Skipped Seeding Accounts: Accounts Have Already Been Seeded");

            return;
        }

        if (await context.Users.NoneAsync(cancellationToken))
        {
            logger.LogDebug("Skipped Seeding Accounts: Users Have Not Yet Been Seeded");

            return;
        }

        if (await context.Clans.NoneAsync(cancellationToken))
        {
            logger.LogDebug("Skipped Seeding Accounts: Clans Have Not Yet Been Seeded");

            return;
        }

        User user = await context.Users.FirstAsync(cancellationToken);
        Clan clan = await context.Clans.FirstAsync(cancellationToken);

        Account systemAccount = new ()
        {
            Name = "KONGOR",
            User = user,
            Type = AccountType.Staff,
            IsMain = true,
            Clan = null,
            ClanTier = ClanTier.None,
            TimestampJoinedClan = null,
            AscensionLevel = 666,
            AutoConnectChatChannels = [.. ChatChannels.AllDefaultChannels],
            SelectedStoreItems = [ "ai.custom_icon:1", "av.Flamboyant", "c.cat_courier", "cc.frostburnlogo", "cr.Punk Creep", "cs.frostburnlogo", "m.Super-Taunt", "sc.paragon_circle_upgrade", "t.Dumpster_Taunt", "te.Punk TP", "w.8bit_ward" ]
        };

        await context.AddAsync(systemAccount, cancellationToken);

        string[] subAccountNames = [ /* [K] */ "ONGOR", "GOPO", "Xen0byte" ];

        foreach (string subAccountName in subAccountNames)
        {
            Account subAccount = new ()
            {
                Name = subAccountName,
                User = user,
                Type = AccountType.Staff,
                IsMain = false,
                Clan = clan,
                ClanTier = subAccountName is /* [K] */ "ONGOR" ? ClanTier.Leader : ClanTier.Officer,
                TimestampJoinedClan = DateTimeOffset.UtcNow,
                AscensionLevel = 666,
                AutoConnectChatChannels = [.. ChatChannels.AllDefaultChannels, clan.GetChatChannelName()],
                SelectedStoreItems = [ "ai.custom_icon:1", "av.Flamboyant", "c.cat_courier", "cc.frostburnlogo", "cr.Punk Creep", "cs.frostburnlogo", "m.Super-Taunt", "sc.paragon_circle_upgrade", "t.Dumpster_Taunt", "te.Punk TP", "w.8bit_ward" ]
            };

            await context.AddAsync(subAccount, cancellationToken);
        }

        string[] hostAccountNames = [ "HOST", "SERVER" ];

        foreach (string hostAccountName in hostAccountNames)
        {
            Account hostAccount = new ()
            {
                Name = hostAccountName,
                User = user,
                Type = AccountType.ServerHost,
                IsMain = false,
                Clan = null,
                ClanTier = ClanTier.None,
                TimestampJoinedClan = null,
                AscensionLevel = 666,
                AutoConnectChatChannels = [ ChatChannels.ServerHostsChannel ],
                SelectedStoreItems = [ "ai.custom_icon:1", "av.Flamboyant", "c.cat_courier", "cc.frostburnlogo", "cr.Punk Creep", "cs.frostburnlogo", "m.Super-Taunt", "sc.paragon_circle_upgrade", "t.Dumpster_Taunt", "te.Punk TP", "w.8bit_ward" ]
            };

            await context.AddAsync(hostAccount, cancellationToken);
        }

        User userGuest = await context.Users.SingleAsync(user => user.EmailAddress.Equals(OOTB.Accounts.GUEST.EmailAddress), cancellationToken: cancellationToken);
        Clan clanGuest = await context.Clans.Skip(1).FirstAsync(cancellationToken);

        Account moderatorAccount = new ()
        {
            Name = "MODERATOR",
            User = userGuest,
            Type = AccountType.MatchModerator,
            IsMain = true,
            Clan = clanGuest,
            ClanTier = ClanTier.Leader,
            TimestampJoinedClan = DateTimeOffset.UtcNow,
            AscensionLevel = 0,
            AutoConnectChatChannels = [ ChatChannels.GuestsChannel, clan.GetChatChannelName() ],
            SelectedStoreItems = [ "ai.custom_icon:1", "av.Flamboyant", "c.cat_courier", "cc.frostburnlogo", "cr.Punk Creep", "cs.frostburnlogo", "m.Super-Taunt", "sc.paragon_circle_upgrade", "t.Dumpster_Taunt", "te.Punk TP", "w.8bit_ward" ]
        };

        await context.AddAsync(moderatorAccount, cancellationToken);

        if (seedBuiltInGuestAccounts)
        {
            for (int iterator = 1; iterator <= 50; iterator++)
            {
                Account guestAccount = new ()
                {
                    Name = $"GUEST-{iterator:D2}",
                    User = userGuest,
                    Type = AccountType.Guest,
                    IsMain = false,
                    Clan = clanGuest,
                    ClanTier = ClanTier.Member,
                    TimestampJoinedClan = DateTimeOffset.UtcNow,
                    AscensionLevel = 0,
                    AutoConnectChatChannels = [ ChatChannels.GuestsChannel, clan.GetChatChannelName() ],
                    SelectedStoreItems = [ "ai.custom_icon:1", "av.Flamboyant", "c.cat_courier", "cc.frostburnlogo", "cr.Punk Creep", "cs.frostburnlogo", "m.Super-Taunt", "sc.paragon_circle_upgrade", "t.Dumpster_Taunt", "te.Punk TP", "w.8bit_ward" ]
                };

                await context.AddAsync(guestAccount, cancellationToken);
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        int seededAccountCount = await context.Accounts.CountAsync(cancellationToken);

        logger.LogInformation("Seeded {AccountCount} Accounts; Built-In Guest Accounts Included: {BuiltInGuestAccountsIncluded}", seededAccountCount, seedBuiltInGuestAccounts);

        // Raise The Skill Rating Of The Non-Guest Staff Accounts Above The Default So They Appear Ranked During Testing

        List<int> staffAccountIDs = await context.Accounts
            .Where(account => account.Type == AccountType.Staff).Select(account => account.ID).ToListAsync(cancellationToken);

        List<AccountStatistics> staffAccountStatistics = await context.AccountStatistics
            .Where(statistics => staffAccountIDs.Contains(statistics.AccountID)).ToListAsync(cancellationToken);

        foreach (AccountStatistics statistics in staffAccountStatistics)
            statistics.SkillRating = 1850.0;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(@"Seeded A Skill Rating Of {SkillRating} For {StaffAccountCount} Staff Accounts", 1850.0, staffAccountIDs.Count);

        // Also Include Friended/Ignored/Banned Peer Seeding To Reduce The Application Startup Duration

        await SeedFriendedPeers(context, cancellationToken, logger);
        await SeedIgnoredPeers(context, cancellationToken, logger);
        await SeedBannedPeers(context, cancellationToken, logger);
    }

    private static async Task SeedFriendedPeers(MerrickContext context, CancellationToken cancellationToken, ILogger logger)
    {
        if (await context.Accounts.NoneAsync(cancellationToken) || await context.Clans.NoneAsync(cancellationToken))
        {
            logger.LogDebug("Skipped Seeding Friended Peers: Accounts Or Clans Have Not Yet Been Seeded");

            return;
        }

        if ((await context.Users.Include(user => user.Accounts).FirstAsync(cancellationToken)).Accounts.Any(account => account.FriendedPeers.Count > 0))
        {
            logger.LogDebug("Skipped Seeding Friended Peers: Friended Peers Have Already Been Seeded");

            return;
        }

        Account systemAccount = await context.Accounts.Include(account => account.User).FirstAsync(cancellationToken);

        List<Account> subAccounts = await context.Accounts.Include(account => account.User).Include(account => account.Clan)
            .Where(account => account.User == systemAccount.User).Where(account => account.IsMain.Equals(false))
            .Where(account => account.Type != AccountType.ServerHost).ToListAsync(cancellationToken: cancellationToken);

        List<Account> hostAccounts = await context.Accounts.Where(account => account.Type == AccountType.ServerHost).ToListAsync(cancellationToken: cancellationToken);

        foreach (Account subAccount in subAccounts)
        {
            subAccount.FriendedPeers = subAccounts.Except([subAccount]).Union([systemAccount]).Select(account => new FriendedPeer
            {
                ID = account.ID,
                Name = account.Name,
                ClanTag = account.Clan?.Tag,
                FriendGroup = "PERSONAS"
            }).ToList();
        }

        foreach (Account hostAccount in hostAccounts)
        {
            hostAccount.FriendedPeers = hostAccounts.Except([hostAccount]).Select(account => new FriendedPeer
            {
                ID = account.ID,
                Name = account.Name,
                ClanTag = account.Clan?.Tag,
                FriendGroup = "CLUSTERS"
            }).ToList();
        }

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(@"Seeded Friended Peers For {AccountCount} Accounts", subAccounts.Count + hostAccounts.Count);
    }

    private static async Task SeedIgnoredPeers(MerrickContext context, CancellationToken cancellationToken, ILogger logger)
    {
        if (await context.Accounts.NoneAsync(cancellationToken))
        {
            logger.LogDebug("Skipped Seeding Ignored Peers: Accounts Have Not Yet Been Seeded");

            return;
        }

        if ((await context.Users.Include(user => user.Accounts).FirstAsync(cancellationToken)).Accounts.Any(account => account.IgnoredPeers.Count > 0))
        {
            logger.LogDebug("Skipped Seeding Ignored Peers: Ignored Peers Have Already Been Seeded");

            return;
        }

        Account systemAccount = await context.Accounts.FirstAsync(cancellationToken);

        List<Account> guestAccounts = await context.Accounts.Include(account => account.User)
            .Where(account => account.IsMain.Equals(false)).Where(account => account.Type == AccountType.Guest).ToListAsync(cancellationToken: cancellationToken);

        foreach (Account guestAccount in guestAccounts)
        {
            systemAccount.IgnoredPeers.Add(new IgnoredPeer
            {
                ID = guestAccount.ID,
                Name = guestAccount.Name
            });
        }

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(@"Seeded {IgnoredPeerCount} Ignored Peers For Account ""{AccountName}""", guestAccounts.Count, systemAccount.Name);
    }

    private static async Task SeedBannedPeers(MerrickContext context, CancellationToken cancellationToken, ILogger logger)
    {
        if (await context.Accounts.NoneAsync(cancellationToken))
        {
            logger.LogDebug("Skipped Seeding Banned Peers: Accounts Have Not Yet Been Seeded");

            return;
        }

        if ((await context.Users.Include(user => user.Accounts).FirstAsync(cancellationToken)).Accounts.Any(account => account.BannedPeers.Count > 0))
        {
            logger.LogDebug("Skipped Seeding Banned Peers: Banned Peers Have Already Been Seeded");

            return;
        }

        Account systemAccount = await context.Accounts.FirstAsync(cancellationToken);

        List<Account> guestAccounts = await context.Accounts.Include(account => account.User)
            .Where(account => account.IsMain.Equals(false)).Where(account => account.Type == AccountType.Guest).ToListAsync(cancellationToken: cancellationToken);

        foreach (Account guestAccount in guestAccounts)
        {
            systemAccount.BannedPeers.Add(new BannedPeer
            {
                ID = guestAccount.ID,
                Name = guestAccount.Name,
                BanReason = "Non-Main Guest Account"
            });
        }

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(@"Seeded {BannedPeerCount} Banned Peers For Account ""{AccountName}""", guestAccounts.Count, systemAccount.Name);
    }

    public static async Task SeedOperator(MerrickContext context, CancellationToken cancellationToken, ILogger logger)
    {
        const string operatorEmailAddress = OOTB.Accounts.OPERATOR.EmailAddress;
        const string operatorAccountName = OOTB.Accounts.OPERATOR.Name;

        if (await context.Accounts.AnyAsync(account => account.Name.Equals(operatorAccountName), cancellationToken))
        {
            logger.LogDebug(@"Skipped Seeding {AccountName} Account: It Has Already Been Seeded", operatorAccountName);

            return;
        }

        if (await context.Roles.NoneAsync(cancellationToken))
        {
            logger.LogDebug(@"Skipped Seeding {AccountName} Account: Roles Have Not Yet Been Seeded", operatorAccountName);

            return;
        }

        Role roleCustodian = await context.Roles.SingleAsync(role => role.Name.Equals(UserRoles.Custodian), cancellationToken);

        User userOperator = new ()
        {
            EmailAddress = operatorEmailAddress,
            Role = roleCustodian,
            SRPPasswordSalt = "81c278a3ed03cf4549e787feac5ffe1d051029e15159b1cf0b087b4f8a85cb69",
            SRPPasswordHash = "bfad34f4dc65064dcae6e2064e12d63b299909b6c0cdb33306a91f26bb624e35",
            PBKDF2PasswordHash = "AQAAAAIAAYagAAAAECx4DCy3qYARlevpaVUJXy28QyvXDcQiJZ1KOSx547WDLBV0pdx2MX5m+Pe3xhtQAg=="
        };

        await context.Users.AddAsync(userOperator, cancellationToken);

        Account accountOperator = new ()
        {
            Name = operatorAccountName,
            User = userOperator,
            Type = AccountType.ServerHost,
            IsMain = true,
            AutoConnectChatChannels = [ ChatChannels.ServerHostsChannel ]
        };

        await context.AddAsync(accountOperator, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(@"Seeded {AccountName} Account With Email Address ""{UserEmailAddress}"" And Custodian Role", operatorAccountName, operatorEmailAddress);
    }

    /// <summary>
    ///     Replaces the publicly-known out-of-box passwords on the built-in administrator, operator, and moderator users when running in Production.
    ///     Existing hashes are left unchanged on subsequent starts when they already represent the configured passwords.
    /// </summary>
    public static async Task SecureBuiltInProductionCredentials(
        MerrickContext context,
        CancellationToken cancellationToken,
        ILogger logger,
        string administratorEmailAddress,
        string administratorPassword,
        string operatorEmailAddress,
        string operatorPassword,
        string moderatorEmailAddress,
        string moderatorPassword)
    {
        ValidateProductionPassword("Administrator", administratorPassword);
        ValidateProductionPassword("Operator", operatorPassword);
        ValidateProductionPassword("Moderator", moderatorPassword);

        if (new[] { administratorPassword, operatorPassword, moderatorPassword }.Distinct(StringComparer.Ordinal).Count() is not 3)
            throw new InvalidOperationException("The Built-In Administrator, Operator, And Moderator Production Passwords Must Be Different");

        if (new[] { administratorEmailAddress, operatorEmailAddress, moderatorEmailAddress }.Distinct(StringComparer.OrdinalIgnoreCase).Count() is not 3)
            throw new InvalidOperationException("The Built-In Administrator, Operator, And Moderator Production Email Addresses Must Be Different");

        Account administratorAccount = await context.Accounts.Include(account => account.User)
            .SingleOrDefaultAsync(account => account.Name.Equals("KONGOR"), cancellationToken)
            ?? throw new InvalidOperationException(@"The Built-In Administrator Account ""KONGOR"" Was Not Found");

        Account operatorAccount = await context.Accounts.Include(account => account.User)
            .SingleOrDefaultAsync(account => account.Name.Equals(OOTB.Accounts.OPERATOR.Name), cancellationToken)
            ?? throw new InvalidOperationException($@"The Built-In Operator Account ""{OOTB.Accounts.OPERATOR.Name}"" Was Not Found");

        Account moderatorAccount = await context.Accounts.Include(account => account.User)
            .SingleOrDefaultAsync(account => account.Name.Equals("MODERATOR"), cancellationToken)
            ?? throw new InvalidOperationException(@"The Built-In Moderator Account ""MODERATOR"" Was Not Found");

        await ValidateProductionEmailAddressAvailability(context, administratorAccount.User, "Administrator", administratorEmailAddress, cancellationToken);
        await ValidateProductionEmailAddressAvailability(context, operatorAccount.User, "Operator", operatorEmailAddress, cancellationToken);
        await ValidateProductionEmailAddressAvailability(context, moderatorAccount.User, "Moderator", moderatorEmailAddress, cancellationToken);

        bool credentialsChanged = false;

        credentialsChanged |= SetEmailAddressIfChanged(administratorAccount.User, administratorEmailAddress);
        credentialsChanged |= SetEmailAddressIfChanged(operatorAccount.User, operatorEmailAddress);
        credentialsChanged |= SetEmailAddressIfChanged(moderatorAccount.User, moderatorEmailAddress);
        credentialsChanged |= SetPasswordIfChanged(administratorAccount.User, administratorPassword);
        credentialsChanged |= SetPasswordIfChanged(operatorAccount.User, operatorPassword);
        credentialsChanged |= SetPasswordIfChanged(moderatorAccount.User, moderatorPassword);

        if (credentialsChanged)
            await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Verified Rotated Production Credentials For Built-In Administrator, Operator, And Moderator Users; Email Addresses: {AdministratorEmailAddress}, {OperatorEmailAddress}, {ModeratorEmailAddress}; Changes Applied: {ChangesApplied}",
            administratorEmailAddress,
            operatorEmailAddress,
            moderatorEmailAddress,
            credentialsChanged);
    }

    private static async Task ValidateProductionEmailAddressAvailability(
        MerrickContext context,
        User builtInUser,
        string credentialName,
        string emailAddress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(emailAddress))
            throw new InvalidOperationException($"{credentialName} Production Email Address Must Be Configured");

        User? userWithRequestedEmailAddress = await context.Users
            .SingleOrDefaultAsync(user => user.EmailAddress.Equals(emailAddress), cancellationToken);

        if (userWithRequestedEmailAddress is not null && userWithRequestedEmailAddress.ID != builtInUser.ID)
            throw new InvalidOperationException($@"{credentialName} Email Address ""{emailAddress}"" Is Already Assigned To Another User");
    }

    private static bool SetEmailAddressIfChanged(User user, string emailAddress)
    {
        if (user.EmailAddress.Equals(emailAddress, StringComparison.OrdinalIgnoreCase))
            return false;

        user.EmailAddress = emailAddress;

        return true;
    }

    private static bool SetPasswordIfChanged(User user, string password)
    {
        bool srpPasswordMatches = user.SRPPasswordSalt.Length is 64
            && SRPPasswordHandlers.ComputeSRPPasswordHash(password, user.SRPPasswordSalt).Equals(user.SRPPasswordHash, StringComparison.Ordinal);

        bool portalPasswordMatches = string.IsNullOrWhiteSpace(user.PBKDF2PasswordHash) is false
            && new PasswordHasher<User>().VerifyHashedPassword(user, user.PBKDF2PasswordHash, password) is PasswordVerificationResult.Success;

        if (srpPasswordMatches && portalPasswordMatches)
            return false;

        string salt = SRPPasswordHandlers.GenerateSRPPasswordSalt();

        user.SRPPasswordSalt = salt;
        user.SRPPasswordHash = SRPPasswordHandlers.ComputeSRPPasswordHash(password, salt);
        user.PBKDF2PasswordHash = new PasswordHasher<User>().HashPassword(user, password);

        return true;
    }

    private static void ValidateProductionPassword(string credentialName, string password)
    {
        if (password.Length < 16
            || password.Any(char.IsUpper) is false
            || password.Any(char.IsLower) is false
            || password.Any(char.IsDigit) is false
            || password.All(char.IsLetterOrDigit))
        {
            throw new InvalidOperationException($"{credentialName} Production Password Must Be At Least 16 Characters And Contain Uppercase, Lowercase, Numeric, And Non-Alphanumeric Characters");
        }

        if (password.Equals(OOTB.Accounts.GUEST.Password, StringComparison.Ordinal)
            || password.Equals(OOTB.Accounts.OPERATOR.Password, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{credentialName} Production Password Cannot Use A Publicly-Known Out-Of-Box Password");
        }
    }

    public static async Task SeedHeroGuides(MerrickContext context, CancellationToken cancellationToken, ILogger logger)
    {
        if (await context.HeroGuides.AnyAsync(cancellationToken))
        {
            logger.LogDebug("Skipped Seeding Hero Guides: Hero Guides Have Already Been Seeded");

            return;
        }

        if (await context.Accounts.NoneAsync(cancellationToken))
        {
            logger.LogDebug("Skipped Seeding Hero Guides: Accounts Have Not Yet Been Seeded");

            return;
        }

        Account author = await context.Accounts.FirstAsync(cancellationToken);

        DeserializationDTOs.GuideGetDTO[] guideDTOs = JsonSerializer.Deserialize<DeserializationDTOs.GuideGetDTO[]>
            (await File.ReadAllTextAsync(DataFiles.HeroGuides, cancellationToken), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        // The Game Client Requires At Least One Featured And One Standard Guide Per Hero

        IEnumerable<HeroGuide> guides = guideDTOs.Select(guide => new HeroGuide()
        {
            Name = guide.Name,
            HeroName = guide.HeroName,
            HeroIdentifier = guide.HeroIdentifier,
            Intro = guide.Intro,
            Content = guide.Content,
            StartingItems = guide.StartingItems.ListToPipeSeparatedString(),
            EarlyGameItems = guide.EarlyGameItems.ListToPipeSeparatedString(),
            CoreItems = guide.CoreItems.ListToPipeSeparatedString(),
            LuxuryItems = guide.LuxuryItems.ListToPipeSeparatedString(),
            AbilityQueue = guide.AbilityQueue.ListToPipeSeparatedString(),
            Author = author,
            Rating = guide.Rating,
            UpVotes = guide.UpVotes,
            DownVotes = guide.DownVotes,
            Public = guide.Public,
            Featured = guide.Featured,
            TimestampCreated = DateTimeOffset.UtcNow
        });

        await context.HeroGuides.AddRangeAsync(guides, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(@"Seeded {HeroGuideCount} Hero Guides Authored By Account ""{AccountName}""", guideDTOs.Length, author.Name);
    }
}
