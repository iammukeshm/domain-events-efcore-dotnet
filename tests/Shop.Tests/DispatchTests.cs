using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shop;
using Testcontainers.PostgreSql;

namespace Shop.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var provider = new ServiceCollection().AddShop(ConnectionString, DispatchMode.Outbox).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ShopDbContext>().Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

public sealed class FakeEmailSender : IEmailSender
{
    public List<string> Sent { get; } = [];
    public int FailuresLeft { get; set; }

    public Task Send(string to, string subject, CancellationToken cancellationToken)
    {
        if (FailuresLeft > 0)
        {
            FailuresLeft--;
            throw new InvalidOperationException("SMTP server unavailable");
        }

        Sent.Add($"{to}: {subject}");
        return Task.CompletedTask;
    }
}

// Simulates the process dying after the email went out, before the commit.
public sealed class CrashOnceHandler(CrashSwitch crash) : DomainEventHandler<OrderPlaced>
{
    protected override Task Handle(OrderPlaced e, CancellationToken cancellationToken) =>
        crash.Armed ? throw new InvalidOperationException("process crashed") : Task.CompletedTask;
}

public sealed class CrashSwitch
{
    public bool Armed { get; set; }
}

public abstract class DispatchTestBase(PostgresFixture db, DispatchMode mode) : IAsyncLifetime
{
    protected FakeEmailSender Email { get; } = new();
    protected CrashSwitch Crash { get; } = new();
    protected FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
    protected ServiceProvider Provider { get; private set; } = null!;
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection()
            .AddShop(db.ConnectionString, mode)
            .AddSingleton<TimeProvider>(Clock)
            .AddSingleton<IEmailSender>(Email)
            .AddSingleton(Crash)
            .AddScoped<IDomainEventHandler, CrashOnceHandler>();
        Provider = services.BuildServiceProvider();

        await using var scope = Provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ShopDbContext>().Database.ExecuteSqlRawAsync(
            """TRUNCATE "Orders", "StockReservations", outbox_messages""", Ct);
    }

    public async ValueTask DisposeAsync() => await Provider.DisposeAsync();

    protected async Task PlaceOrder(string number)
    {
        await using var scope = Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        context.Orders.Add(Order.Place(number, "MUG-01", 2, "ana@example.com"));
        await context.SaveChangesAsync(Ct);
    }

    protected async Task<(int Orders, int Reservations, int Outbox)> Counts()
    {
        await using var scope = Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        return (await context.Orders.CountAsync(Ct),
                await context.StockReservations.CountAsync(Ct),
                await context.OutboxMessages.CountAsync(Ct));
    }
}

[Collection("postgres")]
public sealed class BeforeCommitTests(PostgresFixture db) : DispatchTestBase(db, DispatchMode.BeforeCommit)
{
    [Fact]
    public async Task Order_and_reservation_commit_together()
    {
        await PlaceOrder("A-1");

        Assert.Equal((1, 1, 0), await Counts());
        Assert.Single(Email.Sent);
    }

    [Fact]
    public async Task Failed_commit_still_sends_the_email()
    {
        await PlaceOrder("A-1");

        // Same order number: the unique index rejects the second save.
        await Assert.ThrowsAsync<DbUpdateException>(() => PlaceOrder("A-1"));

        Assert.Equal((1, 1, 0), await Counts());
        Assert.Equal(2, Email.Sent.Count); // one real email, one for an order that does not exist
    }

    [Fact]
    public async Task Email_failure_rolls_back_the_order()
    {
        Email.FailuresLeft = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => PlaceOrder("A-1"));

        Assert.Equal((0, 0, 0), await Counts());
    }
}

[Collection("postgres")]
public sealed class AfterCommitTests(PostgresFixture db) : DispatchTestBase(db, DispatchMode.AfterCommit)
{
    [Fact]
    public async Task Failed_commit_sends_nothing()
    {
        await PlaceOrder("A-1");

        await Assert.ThrowsAsync<DbUpdateException>(() => PlaceOrder("A-1"));

        Assert.Single(Email.Sent);
    }

    [Fact]
    public async Task Email_failure_throws_but_the_order_is_already_saved()
    {
        Email.FailuresLeft = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => PlaceOrder("A-1"));

        // The caller saw an error, yet the order exists and the stock was never reserved.
        Assert.Equal((1, 0, 0), await Counts());
        Assert.Empty(Email.Sent);
    }

    [Fact]
    public async Task Inside_an_explicit_transaction_after_commit_runs_before_the_commit()
    {
        await using (var scope = Provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);

            context.Orders.Add(Order.Place("A-1", "MUG-01", 2, "ana@example.com"));
            await context.SaveChangesAsync(Ct);

            await transaction.RollbackAsync(Ct);
        }

        Assert.Equal((0, 0, 0), await Counts());
        Assert.Single(Email.Sent); // the email went out for a rolled-back order
    }
}

[Collection("postgres")]
public sealed class OutboxTests(PostgresFixture db) : DispatchTestBase(db, DispatchMode.Outbox)
{
    private Task<int> Process() => Provider.GetRequiredService<OutboxProcessor>().ProcessPending(Ct);

    [Fact]
    public async Task Order_and_outbox_message_commit_together_then_get_processed()
    {
        await PlaceOrder("A-1");
        Assert.Equal((1, 0, 1), await Counts());
        Assert.Empty(Email.Sent);

        Assert.Equal(1, await Process());

        Assert.Equal((1, 1, 1), await Counts());
        Assert.Single(Email.Sent);
    }

    [Fact]
    public async Task Failed_commit_leaves_no_message_and_sends_nothing()
    {
        await PlaceOrder("A-1");
        await Assert.ThrowsAsync<DbUpdateException>(() => PlaceOrder("A-1"));

        await Process();

        Assert.Equal((1, 1, 1), await Counts());
        Assert.Single(Email.Sent);
    }

    [Fact]
    public async Task Email_failure_is_retried_on_the_next_run()
    {
        await PlaceOrder("A-1");
        Email.FailuresLeft = 1;

        await Process();
        Assert.Equal((1, 0, 1), await Counts()); // reservation rolled back with the failed attempt
        Assert.Empty(Email.Sent);

        await Process(); // too early: the message waits for its backoff
        Assert.Empty(Email.Sent);

        Clock.Advance(TimeSpan.FromSeconds(31));
        await Process();
        Assert.Equal((1, 1, 1), await Counts());
        Assert.Single(Email.Sent);

        await using var scope = Provider.CreateAsyncScope();
        var message = await scope.ServiceProvider.GetRequiredService<ShopDbContext>().OutboxMessages.SingleAsync(Ct);
        Assert.Equal(2, message.Attempts);
        Assert.NotNull(message.ProcessedAt);
    }

    [Fact]
    public async Task Crash_after_the_email_sends_it_twice()
    {
        await PlaceOrder("A-1");
        Crash.Armed = true;

        await Process(); // email sent, then the "crash" rolls the transaction back
        Assert.Single(Email.Sent);

        Crash.Armed = false;
        Clock.Advance(TimeSpan.FromSeconds(31));
        await Process(); // the message is still pending, so it runs again

        Assert.Equal((1, 1, 1), await Counts());
        Assert.Equal(2, Email.Sent.Count); // at-least-once: handlers must be idempotent
    }
}
