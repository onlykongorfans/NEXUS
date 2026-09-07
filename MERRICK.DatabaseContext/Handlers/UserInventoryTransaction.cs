namespace MERRICK.DatabaseContext.Handlers;

public static class UserInventoryTransaction
{
    /// <summary>
    ///     Serialises store purchases and icon uploads for one user, reloading inventory after acquiring the database lock.
    /// </summary>
    public static Task<TResult> Execute<TResult>(MerrickContext context, int userID, Func<User, Task<TResult>> action, CancellationToken cancellationToken = default)
        => ExecuteForUsers(context, [userID], users => action(users.Single()), cancellationToken);

    public static async Task<TResult> ExecuteForAccount<TResult>(MerrickContext context, int accountID, Func<Account, Task<TResult>> action, CancellationToken cancellationToken = default)
    {
        int userID = await context.Accounts.Where(account => account.ID == accountID).Select(account => account.User.ID).SingleAsync(cancellationToken);

        return await Execute(context, userID, async user =>
        {
            Account account = await context.Accounts.Include(account => account.User)
                .SingleAsync(account => account.ID == accountID && account.User.ID == user.ID, cancellationToken);

            return await action(account);
        }, cancellationToken);
    }

    /// <summary>
    ///     Locks all participating users in ascending ID order before loading or changing any inventory, keeping multi-user rewards atomic.
    /// </summary>
    public static Task<TResult> ExecuteForUsers<TResult>(MerrickContext context, IEnumerable<int> userIDs, Func<IReadOnlyList<User>, Task<TResult>> action, CancellationToken cancellationToken = default)
    {
        int[] orderedUserIDs = userIDs.Distinct().Order().ToArray();

        return context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();

            if (context.Database.IsInMemory())
                return await action(await context.Users.Where(user => orderedUserIDs.Contains(user.ID)).OrderBy(user => user.ID).ToListAsync(cancellationToken));

            await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            List<User> users = [];

            foreach (int userID in orderedUserIDs)
                users.Add(await context.Users.FromSqlInterpolated($"SELECT * FROM [core].[Users] WITH (UPDLOCK, ROWLOCK) WHERE [ID] = {userID}")
                    .SingleAsync(cancellationToken));

            TResult result = await action(users);
            await transaction.CommitAsync(cancellationToken);

            return result;
        });
    }
}
