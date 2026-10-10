using Astra.Core.Models;
using Astra.Gateway.Pipeline;

namespace Astra.Gateway.Tests;

public class AccountSchedulerTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ProviderAccount Account(string id, int order, Action<ProviderAccount>? tweak = null)
    {
        var account = new ProviderAccount { Id = id, ProviderId = "p", DisplayName = id, SortOrder = order, Status = AccountStatus.Active };
        tweak?.Invoke(account);
        return account;
    }

    private static List<ProviderAccount> Three() => [Account("a", 0), Account("b", 1), Account("c", 2)];

    [Fact]
    public void Least_In_Flight_Wins_Then_Least_Recent_Then_Order()
    {
        var scheduler = new AccountScheduler(new Clock());
        var accounts = Three();

        // Nothing in flight, nothing recent: sort order decides.
        using var first = scheduler.Pick("p", accounts, null)!;
        Assert.Equal("a", first.AccountId);
        // a is busy: b, then c.
        using var second = scheduler.Pick("p", accounts, null)!;
        using var third = scheduler.Pick("p", accounts, null)!;
        Assert.Equal(["b", "c"], [second.AccountId, third.AccountId]);

        // Everyone busy once: ties fall to the least recently assigned, then order → a again.
        using var fourth = scheduler.Pick("p", accounts, null)!;
        Assert.Equal("a", fourth.AccountId);
        Assert.Equal(2, scheduler.InFlight("a"));

        // Release b: it is now the least busy.
        second.Dispose();
        second.Dispose(); // idempotent
        Assert.Equal(0, scheduler.InFlight("b"));
        using var fifth = scheduler.Pick("p", accounts, null)!;
        Assert.Equal("b", fifth.AccountId);
    }

    [Fact]
    public void Sequential_Requests_Rotate_Through_Recent_Assignments()
    {
        var scheduler = new AccountScheduler(new Clock());
        var accounts = Three();
        var picked = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            using var lease = scheduler.Pick("p", accounts, null)!; // released straight away: only "recent" differs
            picked.Add(lease.AccountId);
        }
        Assert.Equal(["a", "b", "c", "a", "b", "c"], picked);
    }

    [Fact]
    public void Recent_Assignments_Age_Out_Of_The_Window()
    {
        var clock = new Clock();
        var scheduler = new AccountScheduler(clock);
        var accounts = Three();
        scheduler.Pick("p", accounts, null)!.Dispose(); // a
        scheduler.Pick("p", accounts, null)!.Dispose(); // b
        clock.Now += AccountScheduler.Window + TimeSpan.FromSeconds(1);
        using var lease = scheduler.Pick("p", accounts, null)!;
        Assert.Equal("a", lease.AccountId); // the earlier assignments no longer count
    }

    [Fact]
    public void Unusable_Accounts_Are_Skipped_And_None_Usable_Returns_Null()
    {
        var clock = new Clock();
        var scheduler = new AccountScheduler(clock);
        var accounts = new List<ProviderAccount>
        {
            Account("a", 0, a => a.Enabled = false),
            Account("b", 1, a => a.CooldownUntilUtc = clock.Now.AddMinutes(10)),
            Account("c", 2, a => a.Status = AccountStatus.Revoked),
            Account("d", 3),
        };
        using var lease = scheduler.Pick("p", accounts, null)!;
        Assert.Equal("d", lease.AccountId);

        // Excluded (already tried) accounts too.
        Assert.Null(scheduler.Pick("p", accounts, null, exclude: ["d"]));

        // A cooldown that has passed makes the account usable again.
        clock.Now += TimeSpan.FromMinutes(11);
        using var later = scheduler.Pick("p", accounts, null, exclude: ["d"])!;
        Assert.Equal("b", later.AccountId);
    }

    [Fact]
    public void A_Sticky_Key_Stays_On_Its_Account_Even_When_Another_Is_Idler()
    {
        var scheduler = new AccountScheduler(new Clock());
        var accounts = Three();
        using var held = scheduler.Pick("p", accounts, "pck:one")!;
        Assert.Equal("a", held.AccountId);
        using var other = scheduler.Pick("p", accounts, "pck:two")!;
        Assert.Equal("b", other.AccountId);

        // a is busiest-tied, but the binding wins over the load order.
        using var again = scheduler.Pick("p", accounts, "pck:one")!;
        Assert.Equal("a", again.AccountId);
        Assert.Equal(2, scheduler.InFlight("a"));
        Assert.Equal("a", scheduler.BoundAccount("p", "pck:one"));

        // Keys are scoped by provider.
        using var elsewhere = scheduler.Pick("q", accounts, "pck:one")!;
        Assert.Equal("c", elsewhere.AccountId); // fresh binding under provider q: a and b are busy
    }

    [Fact]
    public void A_Binding_Moves_When_Its_Account_Stops_Being_Usable()
    {
        var clock = new Clock();
        var scheduler = new AccountScheduler(clock);
        var accounts = Three();
        scheduler.Pick("p", accounts, "k")!.Dispose();
        Assert.Equal("a", scheduler.BoundAccount("p", "k"));

        accounts[0].CooldownUntilUtc = clock.Now.AddMinutes(30);
        using var moved = scheduler.Pick("p", accounts, "k")!;
        Assert.NotEqual("a", moved.AccountId);
        Assert.Equal(moved.AccountId, scheduler.BoundAccount("p", "k"));

        // Once a recovers the key does not bounce back: the new binding holds.
        clock.Now += TimeSpan.FromHours(1) - TimeSpan.FromMinutes(1);
        accounts[0].CooldownUntilUtc = null;
        using var stay = scheduler.Pick("p", accounts, "k")!;
        Assert.Equal(moved.AccountId, stay.AccountId);
    }

    [Fact]
    public void Excluding_The_Bound_Account_Rebinds_To_Another()
    {
        var scheduler = new AccountScheduler(new Clock());
        var accounts = Three();
        scheduler.Pick("p", accounts, "k")!.Dispose();
        using var retry = scheduler.Pick("p", accounts, "k", exclude: ["a"])!;
        Assert.NotEqual("a", retry.AccountId);
        Assert.Equal(retry.AccountId, scheduler.BoundAccount("p", "k"));
    }

    [Fact]
    public void Idle_Bindings_Expire_After_The_Ttl()
    {
        var clock = new Clock();
        var scheduler = new AccountScheduler(clock);
        var accounts = Three();
        scheduler.Pick("p", accounts, "k")!.Dispose(); // bound to a
        Assert.Equal("a", scheduler.BoundAccount("p", "k"));
        clock.Now += AccountScheduler.BindingTtl + TimeSpan.FromSeconds(1);
        // Any later sticky pick sweeps the idle binding away.
        scheduler.Pick("p", accounts, "other")!.Dispose();
        Assert.Null(scheduler.BoundAccount("p", "k"));
        using var lease = scheduler.Pick("p", accounts, "k")!;
        Assert.Equal(lease.AccountId, scheduler.BoundAccount("p", "k"));
    }

    [Fact]
    public void A_Touched_Binding_Does_Not_Expire()
    {
        var clock = new Clock();
        var scheduler = new AccountScheduler(clock);
        var accounts = Three();
        scheduler.Pick("p", accounts, "k")!.Dispose(); // a
        for (var i = 0; i < 5; i++)
        {
            clock.Now += TimeSpan.FromMinutes(50); // each touch lands inside the TTL of the previous one
            using var lease = scheduler.Pick("p", accounts, "k")!;
            Assert.Equal("a", lease.AccountId);
        }
    }

    [Fact]
    public async Task Concurrent_New_Sessions_Spread_Over_The_Accounts()
    {
        var scheduler = new AccountScheduler();
        var accounts = Three();
        var leases = await Task.WhenAll(Enumerable.Range(0, 30)
            .Select(i => Task.Run(() => scheduler.Pick("p", accounts, "key-" + i)!)));
        try
        {
            Assert.Equal([10, 10, 10], accounts.Select(a => scheduler.InFlight(a.Id)));
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
        }
        Assert.All(accounts, a => Assert.Equal(0, scheduler.InFlight(a.Id)));
    }

    [Fact]
    public void The_Binding_Table_Is_Bounded()
    {
        var clock = new Clock();
        var scheduler = new AccountScheduler(clock);
        var accounts = Three();
        for (var i = 0; i < AccountScheduler.MaxBindings + 50; i++)
        {
            clock.Now += TimeSpan.FromMilliseconds(1); // distinct "last used" times: eviction is by age
            scheduler.Pick("p", accounts, "k" + i)!.Dispose();
        }
        // The oldest keys were evicted, the newest are still bound.
        Assert.Null(scheduler.BoundAccount("p", "k0"));
        Assert.NotNull(scheduler.BoundAccount("p", "k" + (AccountScheduler.MaxBindings + 49)));
    }
}
