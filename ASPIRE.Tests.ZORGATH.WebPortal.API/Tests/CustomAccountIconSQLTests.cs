namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Tests;

public sealed class CustomAccountIconSQLTests(ZORGATHIntegrationWebApplicationFactory factory)
{
    [Test]
    public async Task Concurrent_Uploads_Consume_One_Entitlement_In_SQL_Server()
    {
        await factory.WithSQLServerContainer().InitialiseAsync();
        DirectoryInfo directory = Directory.CreateTempSubdirectory("nexus-icon-sql-tests-");
        CustomAccountIconConfiguration configuration = new () { StorageDirectory = directory.FullName };

        try
        {
            using IServiceScope seedScope = factory.Services.CreateScope();
            MerrickContext seedContext = seedScope.ServiceProvider.GetRequiredService<MerrickContext>();
            Role role = await seedContext.Roles.SingleAsync(role => role.Name == UserRoles.User);
            User user = new ()
            {
                EmailAddress = "icon-concurrency@example.invalid", Role = role, SRPPasswordHash = "unused", SRPPasswordSalt = "unused",
                PBKDF2PasswordHash = "unused", OwnedStoreItems = [CustomAccountIcons.PendingCode]
            };
            seedContext.Accounts.Add(new Account { Name = "ConcurrentIcon", IsMain = true, User = user });
            await seedContext.SaveChangesAsync();

            using Image<Rgba32> source = new (128, 128, new Rgba32(1, 2, 3, 255));
            using MemoryStream pngStream = new ();
            source.SaveAsPng(pngStream);
            byte[] png = pngStream.ToArray();

            async Task<IActionResult> Submit()
            {
                using IServiceScope scope = factory.Services.CreateScope();
                MerrickContext context = scope.ServiceProvider.GetRequiredService<MerrickContext>();
                CustomAccountIconsController controller = new (context, Options.Create(configuration), new CustomAccountIconProcessor())
                {
                    ControllerContext = new ControllerContext
                    {
                        HttpContext = new DefaultHttpContext
                        {
                            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, user.EmailAddress)], "Test"))
                        }
                    }
                };
                return await controller.Upload(new AccountIconUploadForm
                {
                    Image = new FormFile(new MemoryStream(png), 0, png.Length, "Image", "icon.png"),
                    CropSize = 128, ExpectedSlotID = 1, Confirmed = true
                });
            }

            IActionResult[] results = await Task.WhenAll(Submit(), Submit());
            await Assert.That(results.OfType<OkObjectResult>().Count()).IsEqualTo(1);
            await Assert.That(results.OfType<ConflictObjectResult>().Count()).IsEqualTo(1);
            User reloaded = await seedContext.Users.AsNoTracking().SingleAsync(record => record.ID == user.ID);
            await Assert.That(reloaded.OwnedStoreItems.SequenceEqual(new[] { "ai.custom_icon:1" })).IsTrue();
            await Assert.That(File.Exists(configuration.GetImagePath(user.ID, 1))).IsTrue();
        }
        finally { directory.Delete(recursive: true); }
    }
}
