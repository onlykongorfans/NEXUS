namespace MERRICK.DatabaseContext.Extensions;

public static class CustomAccountIconEndpoints
{
    public static void MapCustomAccountIconImages(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(CustomAccountIconConfiguration.ImageRoute, async (int million, int thousand, int accountID, int slotID,
            MerrickContext context, IOptions<CustomAccountIconConfiguration> options, HttpContext httpContext) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";

            if (accountID <= 0 || slotID <= 0 || million != accountID / 1000000 || thousand != accountID / 1000 % 1000)
                return Results.NotFound();

            User? user = await context.Accounts.AsNoTracking().Where(account => account.ID == accountID)
                .Select(account => account.User).SingleOrDefaultAsync(httpContext.RequestAborted);

            if (user is null || user.OwnedStoreItems.Contains(CustomAccountIcons.GetCode(slotID)) is false)
                return Results.NotFound();

            string path = options.Value.GetImagePath(user.ID, slotID);

            if (File.Exists(path) is false)
                return Results.NotFound();

            httpContext.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            httpContext.Response.Headers.XContentTypeOptions = "nosniff";

            return Results.File(path, "image/png", lastModified: File.GetLastWriteTimeUtc(path));
        }).AllowAnonymous();
    }
}
