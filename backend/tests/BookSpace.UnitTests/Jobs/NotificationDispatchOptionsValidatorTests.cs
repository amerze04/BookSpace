using System.ComponentModel.DataAnnotations;
using BookSpace.Infrastructure.Jobs;

namespace BookSpace.UnitTests.Jobs;

// Hardening pass, finding 8. NotificationDispatchOptionsValidator is what
// ValidateOnStart acts on for the one cross-field rule data annotations
// cannot express — MaxAttempts and BackoffBaseSeconds are each fine alone,
// but their product feeds a BIGINT cast inside NotificationRepository
// .ClaimDueAsync's own claim query, and a config that overflows it would
// crash a live claim rather than fail a boot.
public class NotificationDispatchOptionsValidatorTests
{
    private readonly NotificationDispatchOptionsValidator _validator = new();

    [Fact]
    public void DefaultOptions_AreValid()
    {
        Assert.True(_validator.Validate(null, new NotificationDispatchOptions()).Succeeded);
    }

    [Fact]
    public void MaxAttemptsAtItsOwnUpperRange_IsStillValidAtASmallBackoffBase()
    {
        var options = new NotificationDispatchOptions { MaxAttempts = 100, BackoffBaseSeconds = 1 };

        // 2^100 * 1 vastly exceeds even BIGINT — this is the case the
        // validator exists to refuse. [Range(1, 100)] alone would have let
        // it through.
        Assert.False(_validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void ASmallMaxAttemptsWithAVeryLargeBackoffBase_IsRefused()
    {
        // Each field passes its own [Range] independently; only the product
        // is the problem, which is exactly what a per-field annotation
        // cannot catch.
        var options = new NotificationDispatchOptions { MaxAttempts = 60, BackoffBaseSeconds = int.MaxValue };

        Assert.False(_validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void AReasonableRetryPolicy_IsValid()
    {
        // A deliberately generous but realistic policy: 20 attempts, an
        // hour's base — nowhere near the overflow boundary.
        var options = new NotificationDispatchOptions { MaxAttempts = 20, BackoffBaseSeconds = 3600 };

        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    // Hardening pass, finding 8's other half: LeaseSeconds' floor moved from
    // [Range(1, ...)] to [Range(5, ...)] — a data-annotation change, proven
    // here via the framework's own validator rather than re-implemented.
    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(120, true)]
    public void LeaseSeconds_MustBeAtLeastFive(int leaseSeconds, bool expectedValid)
    {
        var options = new NotificationDispatchOptions { LeaseSeconds = leaseSeconds };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.Equal(expectedValid, isValid);
    }
}
