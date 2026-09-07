namespace ASPIRE.Tests.KONGOR.MasterServer.Tests.Store;

public sealed class CustomAccountIconStoreTests(KONGORIntegrationWebApplicationFactory factory)
{
    [Test]
    public async Task Purchase_Grants_One_Pending_Slot_And_Allows_Another_After_Upload()
    {
        await factory.WithSQLServerContainer().WithDistributedCacheContainer().InitialiseAsync();
        (string cookie, int accountID, int userID) = await RedeemCodeTestsHelper.SeedAuthenticatedSession(factory,
            "custom-icon-store@example.invalid", "CustomIconStore", 2000, 5000, 0);

        async Task<IDictionary<object, object>> Purchase()
        {
            using HttpClient client = factory.CreateClient();
            using FormUrlEncodedContent form = new (new Dictionary<string, string>
            {
                ["cookie"] = cookie, ["account_id"] = accountID.ToString(), ["request_code"] = "4",
                ["product_id"] = "464", ["currency"] = "0", ["category_id"] = "3", ["page"] = "1", ["hostTime"] = "123456"
            });
            using HttpResponseMessage response = await client.PostAsync("/store_requester.php", form);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            return await RedeemCodeTestsHelper.DeserialisePhpResponse(response);
        }

        IDictionary<object, object> first = await Purchase();
        await Assert.That(Convert.ToInt32(first["errorCode"])).IsEqualTo(0);
        await Assert.That(Convert.ToInt32(first["customAccountIcon"])).IsEqualTo(1);
        User purchased = await RedeemCodeTestsHelper.LoadUser(factory, userID);
        await Assert.That(purchased.GoldCoins).IsEqualTo(1650);
        await Assert.That(purchased.OwnedStoreItems.Count(code => code == "ai.custom_icon")).IsEqualTo(1);

        IDictionary<object, object> duplicate = await Purchase();
        await Assert.That(Convert.ToInt32(duplicate["errorCode"]) != 0).IsTrue();
        await Assert.That((await RedeemCodeTestsHelper.LoadUser(factory, userID)).GoldCoins).IsEqualTo(1650);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            MerrickContext context = scope.ServiceProvider.GetRequiredService<MerrickContext>();
            User user = await context.Users.SingleAsync(user => user.ID == userID);
            user.OwnedStoreItems.Remove("ai.custom_icon");
            user.OwnedStoreItems.Add("ai.custom_icon:1");
            await context.SaveChangesAsync();
        }

        IDictionary<object, object> second = await Purchase();
        await Assert.That(Convert.ToInt32(second["errorCode"])).IsEqualTo(0);
        User final = await RedeemCodeTestsHelper.LoadUser(factory, userID);
        await Assert.That(final.GoldCoins).IsEqualTo(1300);
        await Assert.That(final.OwnedStoreItems.Contains("ai.custom_icon:1")).IsTrue();
        await Assert.That(final.OwnedStoreItems.Contains("ai.custom_icon")).IsTrue();
    }
}
