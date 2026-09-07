namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Tests;

public sealed class AccountIconInventoryRaceTests(ZORGATHIntegrationWebApplicationFactory factory)
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Stale_Ticket_Or_Mastery_Writer_Preserves_Completed_Icon_And_Cannot_Reopen_Its_Slot(bool masteryReward)
    {
        await factory.WithSQLServerContainer().WithDistributedCacheContainer().InitialiseAsync();
        DirectoryInfo directory = Directory.CreateTempSubdirectory("nexus-icon-inventory-race-");
        CustomAccountIconConfiguration configuration = new () { StorageDirectory = directory.FullName };

        try
        {
            using IServiceScope seedScope = factory.Services.CreateScope();
            MerrickContext seedContext = seedScope.ServiceProvider.GetRequiredService<MerrickContext>();
            Role role = await seedContext.Roles.SingleAsync(role => role.Name == UserRoles.User);
            User user = new ()
            {
                EmailAddress = "icon-inventory-race@example.invalid", Role = role, SRPPasswordHash = "unused", SRPPasswordSalt = "unused",
                PBKDF2PasswordHash = "unused", OwnedStoreItems = [CustomAccountIcons.PendingCode], PlinkoTickets = 5000
            };
            Account mainAccount = new () { Name = "IconRaceMain", IsMain = true, User = user };
            Account subAccount = new () { Name = "IconRaceSub", IsMain = false, User = user };
            seedContext.Accounts.AddRange(mainAccount, subAccount);
            await seedContext.SaveChangesAsync();

            string cookie = Guid.NewGuid().ToString("N");
            await factory.Services.GetRequiredService<IDatabase>().SetAccountNameForSessionCookie(cookie, subAccount.Name);

            using IServiceScope writerScope = factory.Services.CreateScope();
            MerrickContext writerContext = writerScope.ServiceProvider.GetRequiredService<MerrickContext>();
            // Hold The Exact Snapshot An Overlapping Request Could Have Read Before The Upload Committed
            User staleUser = await writerContext.Users.SingleAsync(record => record.ID == user.ID);

            async Task<IActionResult> Upload(byte colour)
            {
                using IServiceScope uploadScope = factory.Services.CreateScope();
                MerrickContext uploadContext = uploadScope.ServiceProvider.GetRequiredService<MerrickContext>();
                CustomAccountIconsController controller = new (uploadContext, Options.Create(configuration), new CustomAccountIconProcessor())
                {
                    ControllerContext = new ControllerContext
                    {
                        HttpContext = new DefaultHttpContext
                        {
                            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, user.EmailAddress)], "Test"))
                        }
                    }
                };
                using Image<Rgba32> source = new (128, 128, new Rgba32(colour, 2, 3, 255));
                using MemoryStream contents = new ();
                source.SaveAsPng(contents);
                contents.Position = 0;
                return await controller.Upload(new AccountIconUploadForm
                {
                    Image = new FormFile(contents, 0, contents.Length, "Image", "icon.png"),
                    CropSize = 128, ExpectedSlotID = 1, Confirmed = true
                });
            }

            await Assert.That(await Upload(1) is OkObjectResult).IsTrue();
            byte[] originalImage = await File.ReadAllBytesAsync(configuration.GetImagePath(user.ID, 1));
            await Assert.That(staleUser.OwnedStoreItems.SequenceEqual(new[] { CustomAccountIcons.PendingCode })).IsTrue();

            DefaultHttpContext httpContext = new () { RequestServices = writerScope.ServiceProvider };
            httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["cookie"] = cookie, ["id"] = "1792", ["level"] = "2"
            });
            IActionResult response;

            if (masteryReward)
            {
                HeroUsageStatisticsService heroUsage = ActivatorUtilities.CreateInstance<HeroUsageStatisticsService>(writerScope.ServiceProvider);
                ClientRequesterController controller = ActivatorUtilities.CreateInstance<ClientRequesterController>(writerScope.ServiceProvider, heroUsage);
                controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
                httpContext.Request.QueryString = new QueryString("?f=take_mastery_reward");
                response = await controller.ClientRequester();
            }
            else
            {
                TicketExchangeController controller = ActivatorUtilities.CreateInstance<TicketExchangeController>(writerScope.ServiceProvider);
                controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
                response = await controller.Purchase();
            }

            OkObjectResult result = response as OkObjectResult ?? throw new InvalidOperationException("Inventory Request Failed");
            IDictionary<object, object> body = PhpSerialization.Deserialize(result.Value?.ToString() ?? string.Empty) as IDictionary<object, object>
                ?? throw new InvalidOperationException("Inventory Response Was Not A PHP Array");
            await Assert.That(Convert.ToInt32(body[masteryReward ? "error_code" : "status_code"])).IsEqualTo(masteryReward ? 0 : 51);

            User reloaded = await seedContext.Users.AsNoTracking().SingleAsync(record => record.ID == user.ID);
            await Assert.That(reloaded.OwnedStoreItems.Contains("ai.custom_icon:1")).IsTrue();
            await Assert.That(reloaded.OwnedStoreItems.Contains(CustomAccountIcons.PendingCode)).IsFalse();
            await Assert.That(reloaded.OwnedStoreItems.Contains(masteryReward ? "cs.Iron Mastery Badge" : "m.Super-Taunt")).IsTrue();
            await Assert.That(reloaded.PlinkoTickets).IsEqualTo(masteryReward ? 5000 : 3700);
            await Assert.That(await Upload(255) is ConflictObjectResult).IsTrue();
            await Assert.That((await File.ReadAllBytesAsync(configuration.GetImagePath(user.ID, 1))).SequenceEqual(originalImage)).IsTrue();
        }
        finally { directory.Delete(recursive: true); }
    }

    [Test]
    public async Task Multi_User_Inventory_Transactions_Lock_In_Order_And_Roll_Back_Together()
    {
        await factory.WithSQLServerContainer().InitialiseAsync();
        using IServiceScope seedScope = factory.Services.CreateScope();
        MerrickContext seedContext = seedScope.ServiceProvider.GetRequiredService<MerrickContext>();
        Role role = await seedContext.Roles.SingleAsync(role => role.Name == UserRoles.User);
        User[] users = Enumerable.Range(1, 2).Select(index => new User
        {
            EmailAddress = $"multi-inventory-{index}@example.invalid", Role = role, SRPPasswordHash = "unused", SRPPasswordSalt = "unused",
            PBKDF2PasswordHash = "unused", OwnedStoreItems = ["ai.custom_icon:1"]
        }).ToArray();
        seedContext.Users.AddRange(users);
        await seedContext.SaveChangesAsync();
        int[] userIDs = users.Select(user => user.ID).ToArray();

        async Task Grant(IEnumerable<int> requestedUserIDs, string code)
        {
            using IServiceScope scope = factory.Services.CreateScope();
            MerrickContext context = scope.ServiceProvider.GetRequiredService<MerrickContext>();
            await UserInventoryTransaction.ExecuteForUsers(context, requestedUserIDs, async lockedUsers =>
            {
                await Assert.That(lockedUsers.Select(user => user.ID).SequenceEqual(userIDs.Order())).IsTrue();
                foreach (User user in lockedUsers) user.OwnedStoreItems.Add(code);
                await context.SaveChangesAsync();
                return true;
            });
        }

        await Task.WhenAll(Grant(userIDs, "test.reward.one"), Grant(userIDs.Reverse(), "test.reward.two")).WaitAsync(TimeSpan.FromSeconds(30));

        using IServiceScope failureScope = factory.Services.CreateScope();
        MerrickContext failureContext = failureScope.ServiceProvider.GetRequiredService<MerrickContext>();
        bool rolledBack = false;
        try
        {
            await UserInventoryTransaction.ExecuteForUsers<bool>(failureContext, userIDs, async lockedUsers =>
            {
                foreach (User user in lockedUsers) user.OwnedStoreItems.Clear();
                await failureContext.SaveChangesAsync();
                throw new InvalidOperationException("Deliberate Rollback");
            });
        }
        catch (InvalidOperationException exception) when (exception.Message == "Deliberate Rollback") { rolledBack = true; }

        await Assert.That(rolledBack).IsTrue();
        foreach (User user in await seedContext.Users.AsNoTracking().Where(user => userIDs.Contains(user.ID)).ToListAsync())
            await Assert.That(user.OwnedStoreItems.Order().SequenceEqual(new[] { "ai.custom_icon:1", "test.reward.one", "test.reward.two" })).IsTrue();
    }
}
