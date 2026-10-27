using Accounts.Application;
using Accounts.Infrastructure;

namespace Accounts.UnitTests;

/// <summary>The use case on the module's in-memory store: a handle belongs to one account, whatever its case.</summary>
public class HandleUniquenessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly UpdateMyProfileHandler _handler = new(new InMemoryAccountRepository(), TimeProvider.System);

    [Fact]
    public async Task A_handle_taken_in_another_case_is_refused()
    {
        Assert.True((await _handler.Handle(new UpdateMyProfile(Guid.NewGuid(), "Ada", "Ada"), Ct)).IsSuccess);

        var second = await _handler.Handle(new UpdateMyProfile(Guid.NewGuid(), "ADA", "Someone else"), Ct);

        Assert.Equal("handle_taken", Assert.Single(second.Errors).Code);
    }

    [Fact]
    public async Task The_first_request_registers_and_the_next_updates()
    {
        var me = Guid.NewGuid();

        var first = await _handler.Handle(new UpdateMyProfile(me, "grace", "Grace"), Ct);
        var second = await _handler.Handle(new UpdateMyProfile(me, "Grace_Hopper", "Grace Hopper"), Ct);

        Assert.True(first.Value.Registered);
        Assert.False(second.Value.Registered);
        Assert.Equal("Grace_Hopper", second.Value.Profile.Handle);
    }

    [Fact]
    public async Task An_account_may_change_the_case_of_its_own_handle()
    {
        var me = Guid.NewGuid();
        await _handler.Handle(new UpdateMyProfile(me, "linus", "Linus"), Ct);

        var renamed = await _handler.Handle(new UpdateMyProfile(me, "Linus", "Linus"), Ct);

        Assert.Equal("Linus", renamed.Value.Profile.Handle);
    }

    [Fact]
    public async Task A_handle_given_up_can_be_taken_by_someone_else()
    {
        await _handler.Handle(new UpdateMyProfile(Guid.NewGuid(), "margaret", "M"), Ct);
        var first = Guid.NewGuid();
        await _handler.Handle(new UpdateMyProfile(first, "barbara", "B"), Ct);
        await _handler.Handle(new UpdateMyProfile(first, "barbara_l", "B"), Ct);

        var taken = await _handler.Handle(new UpdateMyProfile(Guid.NewGuid(), "Barbara", "Another B"), Ct);

        Assert.True(taken.IsSuccess);
    }
}
