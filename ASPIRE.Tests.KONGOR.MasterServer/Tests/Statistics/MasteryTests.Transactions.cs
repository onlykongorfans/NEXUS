namespace ASPIRE.Tests.KONGOR.MasterServer.Tests.Statistics;

public sealed partial class MasteryTests_Integration
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Failed_Or_Cancelled_Commit_Does_Not_Publish_A_Boost_And_Allows_Retry(bool cancelCommit)
    {
        (Account account, string cookie) = await SeedBoostTransaction("BoostCommit");
        using CancellationTokenSource cancellation = new ();
        IDatabase cache = CreateBoostCacheProxy(out BoostCacheProxy proxy);
        CommitFailureInterceptor interceptor = new (cancelCommit ? cancellation : null);
        bool failed = false;

        try { await ExecuteBoost(cookie, cache, interceptor, cancellation.Token); }
        catch (OperationCanceledException) when (cancelCommit) { failed = true; }
        catch (InvalidOperationException exception) when (exception.Message == "Simulated Commit Failure") { failed = true; }

        await Assert.That(failed).IsTrue();
        await Assert.That(proxy.PublicationAttempts).IsEqualTo(0);
        await Assert.That(await cache.GetMasteryBoostContext(account.ID, 1)).IsNull();
        await AssertBoostState(account, expectedExperience: 200, expectedBoosts: 2, expectedMarker: null);
        await Assert.That(BoostErrorCode(await ExecuteBoost(cookie))).IsEqualTo(0);
        await AssertBoostState(account, expectedExperience: 600, expectedBoosts: 1, expectedMarker: 400);
    }

    [Test]
    public async Task A_Second_Boost_Is_Rejected_After_Commit_Before_Cache_Publication()
    {
        (Account account, string cookie) = await SeedBoostTransaction("BoostOverlap");
        IDatabase cache = CreateBoostCacheProxy(out BoostCacheProxy proxy);
        TaskCompletionSource publicationStarted = new (TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releasePublication = new (TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.BeforePublication = async () =>
        {
            publicationStarted.TrySetResult();
            await releasePublication.Task.WaitAsync(TimeSpan.FromSeconds(30));
        };

        Task<IActionResult> first = ExecuteBoost(cookie, cache);
        try
        {
            await publicationStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await AssertBoostState(account, expectedExperience: 600, expectedBoosts: 1, expectedMarker: 400);
            await Assert.That(await cache.GetMasteryBoostContext(account.ID, 1)).IsNull();
            IActionResult second = await ExecuteBoost(cookie).WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(BoostErrorCode(second)).IsEqualTo(4);
        }
        finally
        {
            releasePublication.TrySetResult();
            await first;
        }

        await Assert.That(BoostErrorCode(await first)).IsEqualTo(0);
        await Assert.That(proxy.PublicationAttempts).IsEqualTo(1);
        await AssertBoostState(account, expectedExperience: 600, expectedBoosts: 1, expectedMarker: 400);
    }

    [Test]
    public async Task Cache_Publication_Failure_Preserves_Success_Reporting_And_Duplicate_Prevention()
    {
        (Account account, string cookie) = await SeedBoostTransaction("BoostCacheFail");
        IDatabase cache = CreateBoostCacheProxy(out BoostCacheProxy proxy);
        proxy.FailPublication = true;

        await Assert.That(BoostErrorCode(await ExecuteBoost(cookie, cache))).IsEqualTo(0);
        await Assert.That(proxy.PublicationAttempts).IsEqualTo(1);
        await Assert.That(await cache.GetMasteryBoostContext(account.ID, 1)).IsNull();
        await AssertBoostState(account, expectedExperience: 600, expectedBoosts: 1, expectedMarker: 400);

        proxy.FailReads = true;
        await Assert.That(BoostErrorCode(await ExecuteBoost(cookie, cache))).IsEqualTo(4);

        using HttpResponseMessage response = await PostClientRequest("get_match_stats", new Dictionary<string, string>
        {
            ["cookie"] = cookie, ["match_id"] = "1"
        });
        IDictionary<object, object> body = await PlinkoTestsHelper.DeserialisePhpResponse(response);
        IDictionary<object, object> mastery = (IDictionary<object, object>) body["match_mastery"];
        await Assert.That(Convert.ToInt32(mastery["mastery_exp_boost"])).IsEqualTo(400);
        await Assert.That(Convert.ToInt32(mastery["mastery_exp_original"])).IsEqualTo(0);
        await Assert.That(Convert.ToBoolean(mastery["mastery_canboost"])).IsFalse();
        await Assert.That(Convert.ToBoolean(mastery["mastery_super_canboost"])).IsFalse();
    }

    [Test]
    public async Task Legacy_Cached_Boost_Is_Preserved_Without_Consuming_Another_Boost()
    {
        (Account account, string cookie) = await SeedBoostTransaction("BoostLegacy");
        IDatabase cache = CreateBoostCacheProxy(out BoostCacheProxy proxy);
        await cache.SetMasteryBoostContext(account.ID, 1, new MasteryBoostContext(400, false));
        await SetHeroExperience(account, "Hero_Accursed", 600);

        await Assert.That(BoostErrorCode(await ExecuteBoost(cookie, cache))).IsEqualTo(4);
        await AssertBoostState(account, expectedExperience: 600, expectedBoosts: 2, expectedMarker: 400);

        proxy.FailReads = true;
        await Assert.That(BoostErrorCode(await ExecuteBoost(cookie, cache))).IsEqualTo(4);
        await Assert.That(proxy.PublicationAttempts).IsEqualTo(1);
    }

    private async Task<(Account Account, string Cookie)> SeedBoostTransaction(string accountName)
    {
        (Account account, string cookie) = await SeedAuthenticatedAccount($"{accountName}@example.invalid", accountName);
        await SeedRankedMatch(account, matchID: 1, heroIdentifier: "Hero_Accursed", heroLevel: 10);
        await SetHeroExperience(account, "Hero_Accursed", 200);
        await GrantMasteryBoosts(account, regularBoosts: 2, superBoosts: 0);
        return (account, cookie);
    }

    private async Task<IActionResult> ExecuteBoost(string cookie, IDatabase? cache = null,
        DbTransactionInterceptor? interceptor = null, CancellationToken cancellationToken = default)
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();
        DbContextOptionsBuilder<MerrickContext> options = new (scope.ServiceProvider.GetRequiredService<DbContextOptions<MerrickContext>>());
        if (interceptor is not null) options.AddInterceptors(interceptor);
        await using MerrickContext context = new (options.Options);
        cache ??= scope.ServiceProvider.GetRequiredService<IDatabase>();
        HeroUsageStatisticsService heroUsage = new (context, cache);
        ClientRequesterController controller = ActivatorUtilities.CreateInstance<ClientRequesterController>(scope.ServiceProvider, context, cache, heroUsage);
        DefaultHttpContext httpContext = new () { RequestServices = scope.ServiceProvider, RequestAborted = cancellationToken };
        httpContext.Request.QueryString = new QueryString("?f=boost_match_mastery");
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["cookie"] = cookie, ["match_id"] = "1", ["is_super_boost"] = "0"
        });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return await controller.ClientRequester();
    }

    private async Task AssertBoostState(Account account, int expectedExperience, int expectedBoosts, int? expectedMarker)
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();
        MerrickContext context = scope.ServiceProvider.GetRequiredService<MerrickContext>();
        User user = await context.Users.SingleAsync(user => user.ID == account.User.ID);
        Mastery mastery = await context.Masteries.SingleAsync(mastery => mastery.AccountID == account.ID);
        MatchParticipantStatistics participant = await context.MatchParticipantStatistics.SingleAsync(participant => participant.AccountID == account.ID && participant.MatchID == 1);
        await Assert.That(MasteryConsumables.MasteryBoostsOwned(user)).IsEqualTo(expectedBoosts);
        await Assert.That(mastery.GetHeroExperienceByHeroIdentifier("Hero_Accursed")).IsEqualTo(expectedExperience);
        await Assert.That(participant.MasteryBoostExperience).IsEqualTo(expectedMarker);
    }

    private IDatabase CreateBoostCacheProxy(out BoostCacheProxy proxy)
    {
        IDatabase cache = DispatchProxy.Create<IDatabase, BoostCacheProxy>();
        proxy = (BoostCacheProxy) cache;
        proxy.Inner = webApplicationFactory.Services.GetRequiredService<IDatabase>();
        return cache;
    }

    private static int BoostErrorCode(IActionResult response)
    {
        OkObjectResult result = response as OkObjectResult ?? throw new InvalidOperationException("Unexpected Boost Response");
        IDictionary<object, object> body = PhpSerialization.Deserialize(result.Value?.ToString() ?? string.Empty) as IDictionary<object, object>
            ?? throw new InvalidOperationException("Boost Response Was Not A PHP Array");
        return Convert.ToInt32(body["error_code"]);
    }

    private sealed class CommitFailureInterceptor(CancellationTokenSource? cancellation) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (cancellation is not null)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            throw new InvalidOperationException("Simulated Commit Failure");
        }
    }

    public class BoostCacheProxy : DispatchProxy
    {
        public IDatabase? Inner { get; set; }
        public bool FailPublication { get; set; }
        public bool FailReads { get; set; }
        public int PublicationAttempts { get; private set; }
        public Func<Task>? BeforePublication { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method is null) throw new InvalidOperationException("Missing Cache Method");
            IDatabase inner = Inner ?? throw new InvalidOperationException("Missing Inner Cache");
            bool boostKey = arguments is { Length: > 0 } && arguments[0] is RedisKey key && key.ToString().Contains("MASTERY-BOOST-CONTEXT");
            if (boostKey && method.Name == "StringSetAsync") return Publish(method, arguments, inner);
            if (boostKey && FailReads && method.Name == "StringGetAsync")
                return Task.FromException<RedisValue>(new RedisConnectionException(ConnectionFailureType.SocketFailure, "Simulated Cache Read Failure"));
            return method.Invoke(inner, arguments);
        }

        private async Task<bool> Publish(MethodInfo method, object?[]? arguments, IDatabase inner)
        {
            PublicationAttempts++;
            if (BeforePublication is not null) await BeforePublication();
            if (FailPublication) throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "Simulated Cache Publication Failure");
            return await (method.Invoke(inner, arguments) as Task<bool> ?? throw new InvalidOperationException("Unexpected Cache Publication Result"));
        }
    }
}
