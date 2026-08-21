using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.UnitTests;

public class ApprovalRequestTests
{
    private static readonly DateTime NowUtc = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    private static ApprovalRequest CreateValid() =>
        new(Guid.NewGuid(), Guid.NewGuid(), NowUtc, NowUtc.AddHours(24));

    [Fact]
    public void Constructor_StartsPending()
    {
        var request = CreateValid();

        Assert.Equal(ApprovalDecision.Pending, request.Decision);
        Assert.Null(request.DecidedByUserId);
        Assert.Null(request.DecidedAtUtc);
    }

    [Fact]
    public void Decide_SetsDecisionAndAuditFields()
    {
        var request = CreateValid();
        var deciderId = Guid.NewGuid();
        var decidedAt = NowUtc.AddHours(1);

        request.Decide(ApprovalDecision.Approved, deciderId, decidedAt, "Looks good");

        Assert.Equal(ApprovalDecision.Approved, request.Decision);
        Assert.Equal(deciderId, request.DecidedByUserId);
        Assert.Equal(decidedAt, request.DecidedAtUtc);
        Assert.Equal("Looks good", request.Note);
    }

    [Fact]
    public void Decide_Throws_WhenAlreadyDecided()
    {
        var request = CreateValid();
        request.Decide(ApprovalDecision.Approved, Guid.NewGuid(), NowUtc.AddHours(1), null);

        Assert.Throws<InvalidOperationException>(() =>
            request.Decide(ApprovalDecision.Rejected, Guid.NewGuid(), NowUtc.AddHours(2), null));
    }

    [Fact]
    public void Decide_Throws_WhenDecisionIsPending()
    {
        var request = CreateValid();

        Assert.Throws<ArgumentException>(() =>
            request.Decide(ApprovalDecision.Pending, Guid.NewGuid(), NowUtc.AddHours(1), null));
    }

    [Fact]
    public void Expire_SetsDecisionExpired_WithNoHumanDecider()
    {
        var request = CreateValid();
        var expiredAt = NowUtc.AddHours(24);

        request.Expire(expiredAt);

        Assert.Equal(ApprovalDecision.Expired, request.Decision);
        Assert.Null(request.DecidedByUserId);
        Assert.Equal(expiredAt, request.DecidedAtUtc);
    }

    [Fact]
    public void Expire_Throws_WhenAlreadyDecided()
    {
        var request = CreateValid();
        request.Decide(ApprovalDecision.Rejected, Guid.NewGuid(), NowUtc.AddHours(1), null);

        Assert.Throws<InvalidOperationException>(() => request.Expire(NowUtc.AddHours(24)));
    }
}
