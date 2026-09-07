namespace ZORGATH.WebPortal.API.Controllers;

[ApiController]
[Route("CustomAccountIcons")]
[Authorize(Policy = UserRoles.AllRoles)]
public sealed class CustomAccountIconsController(MerrickContext context, IOptions<CustomAccountIconConfiguration> options,
    CustomAccountIconProcessor processor) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List()
    {
        User? user = await FindUser();

        if (user is null)
            return Unauthorized();

        int mainAccountID = await context.Accounts.Where(account => account.User.ID == user.ID && account.IsMain)
            .Select(account => account.ID).SingleAsync(HttpContext.RequestAborted);

        return Ok(new
        {
            HasPendingSlot = user.OwnedStoreItems.Contains(CustomAccountIcons.PendingCode),
            NextSlotID = CustomAccountIcons.GetNextSlot(user),
            Icons = CustomAccountIcons.GetSlots(user).Select(slotID => new
            {
                SlotID = slotID,
                ImageURL = options.Value.GetImageURL(mainAccountID, slotID),
                Available = System.IO.File.Exists(options.Value.GetImagePath(user.ID, slotID))
            })
        });
    }

    [HttpPost("Preview")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(CustomAccountIconConfiguration.MaximumUploadBytes + 65536)]
    [RequestFormLimits(MultipartBodyLengthLimit = CustomAccountIconConfiguration.MaximumUploadBytes + 65536)]
    public async Task<IActionResult> Preview([FromForm] AccountIconUploadForm form)
    {
        User? user = await FindUser();

        if (user is null)
            return Unauthorized();

        if (user.OwnedStoreItems.Contains(CustomAccountIcons.PendingCode) is false)
            return Conflict("Purchase a Custom Account Icon slot in the game store before uploading.");

        try
        {
            ProcessedAccountIcon image = await Process(form);
            return Ok(new { Image = Convert.ToBase64String(image.PNG), image.Width, image.Height, image.CropX, image.CropY, image.CropSize });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    [HttpPost]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting(RateLimiterPolicies.Strict)]
    [RequestSizeLimit(CustomAccountIconConfiguration.MaximumUploadBytes + 65536)]
    [RequestFormLimits(MultipartBodyLengthLimit = CustomAccountIconConfiguration.MaximumUploadBytes + 65536)]
    public async Task<IActionResult> Upload([FromForm] AccountIconUploadForm form)
    {
        User? authenticatedUser = await FindUser();

        if (authenticatedUser is null)
            return Unauthorized();

        if (form.Confirmed is false || form.ExpectedSlotID <= 0)
            return BadRequest("Preview and confirm the image before using your purchased slot.");

        ProcessedAccountIcon image;

        try { image = await Process(form); }
        catch (ArgumentException exception) { return BadRequest(exception.Message); }

        return await UserInventoryTransaction.Execute<IActionResult>(context, authenticatedUser.ID, async user =>
        {
            if (user.OwnedStoreItems.Contains(CustomAccountIcons.PendingCode) is false || CustomAccountIcons.GetNextSlot(user) != form.ExpectedSlotID)
                return Conflict("This upload slot has already been used or changed. Refresh your icon collection.");

            int slotID = form.ExpectedSlotID;
            string path = options.Value.GetImagePath(user.ID, slotID);
            string directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Icon Directory Is Missing");
            Directory.CreateDirectory(directory);
            string temporaryPath = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");

            try
            {
                await using (FileStream stream = new (temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await stream.WriteAsync(image.PNG, HttpContext.RequestAborted);
                    stream.Flush(flushToDisk: true);
                }

                // Only Unowned Slots Reach This Point; A Retry May Replace An Unpublished File Left By A Failed Database Commit
                System.IO.File.Move(temporaryPath, path, overwrite: true);
                user.OwnedStoreItems.Remove(CustomAccountIcons.PendingCode);
                user.OwnedStoreItems.Add(CustomAccountIcons.GetCode(slotID));
                await context.SaveChangesAsync(HttpContext.RequestAborted);
            }
            finally
            {
                if (System.IO.File.Exists(temporaryPath))
                    System.IO.File.Delete(temporaryPath);
            }

            return Ok(new { SlotID = slotID });
        }, HttpContext.RequestAborted);
    }

    private Task<User?> FindUser()
    {
        string email = User.Claims.GetUserEmailAddress();
        return context.Users.AsNoTracking().SingleOrDefaultAsync(user => user.EmailAddress == email, HttpContext.RequestAborted);
    }

    private async Task<ProcessedAccountIcon> Process(AccountIconUploadForm form)
    {
        if (form.Image is null || form.Image.Length == 0 || form.Image.Length > CustomAccountIconConfiguration.MaximumUploadBytes)
            throw new ArgumentException("Upload a PNG file no larger than 2 MB.");

        using MemoryStream contents = new ();
        await form.Image.CopyToAsync(contents, HttpContext.RequestAborted);
        return processor.Process(contents.ToArray(), form.CropX, form.CropY, form.CropSize);
    }
}

public sealed class AccountIconUploadForm
{
    public IFormFile? Image { get; set; }
    public int CropX { get; set; }
    public int CropY { get; set; }
    public int CropSize { get; set; }
    public int ExpectedSlotID { get; set; }
    public bool Confirmed { get; set; }
}
