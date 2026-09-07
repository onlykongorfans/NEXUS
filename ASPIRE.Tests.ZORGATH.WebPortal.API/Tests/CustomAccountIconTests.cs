namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Tests;

public sealed class CustomAccountIconTests
{
    [Test]
    public async Task Cropping_Produces_128_Pixel_RGBA_PNG_And_Preserves_Transparency()
    {
        using Image<Rgba32> source = new (256, 128, new Rgba32(255, 0, 0, 255));
        for (int row = 0; row < 128; row++)
            for (int column = 128; column < 256; column++)
                source[column, row] = new Rgba32(0, 0, 255, 96);
        using MemoryStream input = new ();
        source.SaveAsPng(input);

        ProcessedAccountIcon result = new CustomAccountIconProcessor().Process(input.ToArray(), 128, 0, 128);
        using Image<Rgba32> decoded = Image.Load<Rgba32>(result.PNG);

        await Assert.That(decoded.Width).IsEqualTo(128);
        await Assert.That(decoded.Height).IsEqualTo(128);
        await Assert.That(decoded[0, 0]).IsEqualTo(new Rgba32(0, 0, 255, 96));
        await Assert.That(result.PNG[24]).IsEqualTo((byte) 8);
        await Assert.That(result.PNG[25]).IsEqualTo((byte) 6);
        await Assert.That(result.PNG.Length < CustomAccountIconConfiguration.MaximumUploadBytes).IsTrue();
    }

    [Test]
    [Arguments(64, 128)]
    [Arguments(2049, 128)]
    public async Task Invalid_Dimensions_Are_Rejected(int width, int height)
    {
        byte[] input = CreatePNG(width, height);
        await Assert.That(() => new CustomAccountIconProcessor().Process(input)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Oversized_Corrupt_Animated_And_Out_Of_Bounds_Images_Are_Rejected()
    {
        CustomAccountIconProcessor processor = new ();
        await Assert.That(() => processor.Process(new byte[CustomAccountIconConfiguration.MaximumUploadBytes + 1])).Throws<ArgumentException>();
        await Assert.That(() => processor.Process("not a png"u8.ToArray())).Throws<ArgumentException>();
        byte[] valid = CreatePNG();
        await Assert.That(() => processor.Process(valid, 1, 0, 128)).Throws<ArgumentException>();

        // An Animation Control Chunk Must Be Rejected Before Pixel Decoding
        byte[] animated = new byte[valid.Length + 20];
        valid.AsSpan(0, 33).CopyTo(animated);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(animated.AsSpan(33), 8);
        "acTL"u8.CopyTo(animated.AsSpan(37));
        valid.AsSpan(33).CopyTo(animated.AsSpan(53));
        await Assert.That(() => processor.Process(animated)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Preview_Does_Not_Consume_Slot_And_Upload_Is_Shared_And_Cannot_Be_Replayed()
    {
        using IconTestContext test = new ();
        await test.Seed();
        AccountIconUploadForm form = CreateForm();

        await Assert.That(await test.Controller.Preview(form)).IsTypeOf<OkObjectResult>();
        User before = await test.Context.Users.AsNoTracking().SingleAsync();
        await Assert.That(before.OwnedStoreItems.Contains(CustomAccountIcons.PendingCode)).IsTrue();
        await Assert.That(Directory.GetFiles(test.Directory.FullName, "*", SearchOption.AllDirectories).Length).IsEqualTo(0);

        await Assert.That(await test.Controller.Upload(form)).IsTypeOf<OkObjectResult>();
        Account sibling = await test.Context.Accounts.Include(account => account.User).SingleAsync(account => !account.IsMain);
        await Assert.That(sibling.User.OwnedStoreItems.Contains("ai.custom_icon:1")).IsTrue();
        await Assert.That(sibling.User.OwnedStoreItems.Contains(CustomAccountIcons.PendingCode)).IsFalse();
        await Assert.That(File.Exists(test.Configuration.GetImagePath(1, 1))).IsTrue();
        byte[] original = await File.ReadAllBytesAsync(test.Configuration.GetImagePath(1, 1));

        // A Delayed Duplicate Must Not Consume A Subsequently Purchased Slot
        sibling.User.OwnedStoreItems.Add(CustomAccountIcons.PendingCode);
        await test.Context.SaveChangesAsync();
        await Assert.That(await test.Controller.Upload(form)).IsTypeOf<ConflictObjectResult>();
        User after = await test.Context.Users.AsNoTracking().SingleAsync();
        await Assert.That(after.OwnedStoreItems.Contains(CustomAccountIcons.PendingCode)).IsTrue();
        await Assert.That((await File.ReadAllBytesAsync(test.Configuration.GetImagePath(1, 1))).SequenceEqual(original)).IsTrue();

        form.ExpectedSlotID = 2;
        await Assert.That(await test.Controller.Upload(form)).IsTypeOf<OkObjectResult>();
        await Assert.That(File.Exists(test.Configuration.GetImagePath(1, 2))).IsTrue();
    }

    [Test]
    public async Task Invalid_Upload_And_Missing_Confirmation_Leave_Entitlement_Unused()
    {
        using IconTestContext test = new ();
        await test.Seed();
        AccountIconUploadForm form = CreateForm();
        form.Confirmed = false;
        await Assert.That(await test.Controller.Upload(form)).IsTypeOf<BadRequestObjectResult>();
        form.Confirmed = true;
        form.CropX = 200;
        await Assert.That(await test.Controller.Upload(form)).IsTypeOf<BadRequestObjectResult>();
        User user = await test.Context.Users.AsNoTracking().SingleAsync();
        await Assert.That(user.OwnedStoreItems.Contains(CustomAccountIcons.PendingCode)).IsTrue();
    }

    [Test]
    public async Task Another_User_Cannot_Use_The_Owners_Pending_Slot()
    {
        using IconTestContext test = new ();
        await test.Seed();
        test.Controller.HttpContext.User = Principal("someone-else@example.invalid");
        await Assert.That(await test.Controller.Upload(CreateForm())).IsTypeOf<UnauthorizedResult>();
        await Assert.That(Directory.GetFiles(test.Directory.FullName, "*", SearchOption.AllDirectories).Length).IsEqualTo(0);
    }

    [Test]
    public async Task Main_And_Subaccount_Image_URLs_Serve_The_Same_PNG_And_Reject_Invalid_Paths()
    {
        using IconTestContext test = new ();
        await test.Seed();
        await test.Controller.Upload(CreateForm());

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(test.Context);
        builder.Services.AddSingleton<IOptions<CustomAccountIconConfiguration>>(Options.Create(test.Configuration));
        await using WebApplication application = builder.Build();
        application.MapCustomAccountIconImages();
        await application.StartAsync();
        using HttpClient client = application.GetTestClient();

        using HttpResponseMessage main = await client.GetAsync("/icons/1/200/1200456/1.cai");
        using HttpResponseMessage sibling = await client.GetAsync("/icons/1/200/1200457/1.cai");
        await Assert.That(main.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(sibling.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(main.Content.Headers.ContentType?.MediaType).IsEqualTo("image/png");
        byte[] mainImage = await main.Content.ReadAsByteArrayAsync();
        byte[] siblingImage = await sibling.Content.ReadAsByteArrayAsync();
        await Assert.That(mainImage.SequenceEqual(siblingImage)).IsTrue();

        foreach (string path in new[] { "/icons/0/200/1200456/1.cai", "/icons/1/200/1200456/2.cai", "/icons/1/200/1200458/1.cai", "/icons/1/200/1200456/0.cai" })
        {
            using HttpResponseMessage response = await client.GetAsync(path);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
        }
    }

    private static byte[] CreatePNG(int width = 128, int height = 128)
    {
        using Image<Rgba32> image = new (width, height, new Rgba32(10, 20, 30, 128));
        using MemoryStream stream = new ();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private static AccountIconUploadForm CreateForm()
    {
        byte[] png = CreatePNG();
        return new AccountIconUploadForm
        {
            Image = new FormFile(new MemoryStream(png), 0, png.Length, "Image", "icon.png"),
            CropSize = 128,
            ExpectedSlotID = 1,
            Confirmed = true
        };
    }

    private static ClaimsPrincipal Principal(string email) => new (new ClaimsIdentity([new Claim(ClaimTypes.Email, email)], "Test"));

    private sealed class IconTestContext : IDisposable
    {
        public DirectoryInfo Directory { get; } = System.IO.Directory.CreateTempSubdirectory("nexus-icon-tests-");
        public MerrickContext Context { get; } = new (new DbContextOptionsBuilder<MerrickContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public CustomAccountIconConfiguration Configuration { get; }
        public CustomAccountIconsController Controller { get; }

        public IconTestContext()
        {
            Configuration = new () { StorageDirectory = Directory.FullName };
            Controller = new (Context, Options.Create(Configuration), new CustomAccountIconProcessor())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal("icons@example.invalid") } }
            };
        }

        public async Task Seed()
        {
            User user = new ()
            {
                ID = 1, EmailAddress = "icons@example.invalid", Role = new Role { ID = 1, Name = UserRoles.User },
                SRPPasswordSalt = "unused", SRPPasswordHash = "unused", PBKDF2PasswordHash = "unused", OwnedStoreItems = [CustomAccountIcons.PendingCode]
            };
            Context.Accounts.AddRange(new Account { ID = 1200456, Name = "IconMain", IsMain = true, User = user },
                new Account { ID = 1200457, Name = "IconSub", IsMain = false, User = user });
            await Context.SaveChangesAsync();
        }

        public void Dispose()
        {
            Context.Dispose();
            Directory.Delete(recursive: true);
        }
    }
}
