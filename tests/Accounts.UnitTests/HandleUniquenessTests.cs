using Accounts.Application;
using Accounts.Contracts;

namespace Accounts.UnitTests;

/// <summary>The use case on a store in memory: a handle belongs to one account, whatever its case.</summary>
public class HandleUniquenessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly RecordingOutbox _outbox = new();

    private readonly UpdateMyProfileHandler _handler;

    public HandleUniquenessTests() => _handler = new(new InMemoryAccounts(), TimeProvider.System, new NoTransaction(), _outbox);

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

    [Fact]
    public async Task Registering_announces_AccountRegistered_and_an_update_ProfileUpdated_but_a_repeat_nothing()
    {
        var me = Guid.NewGuid();

        await _handler.Handle(new UpdateMyProfile(me, "alan", "Alan"), Ct);
        await _handler.Handle(new UpdateMyProfile(me, "alan", "Alan"), Ct);
        await _handler.Handle(new UpdateMyProfile(me, "alan_t", "Alan Turing"), Ct);

        Assert.Collection(_outbox.Events,
            e => Assert.Equal(new { AccountId = me, Handle = "alan" }, new { ((AccountRegistered)e).AccountId, ((AccountRegistered)e).Handle }),
            e => Assert.Equal("Alan Turing", Assert.IsType<ProfileUpdated>(e).DisplayName));
    }
}
