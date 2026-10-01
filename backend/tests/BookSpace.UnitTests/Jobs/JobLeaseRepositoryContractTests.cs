using BookSpace.Application.Abstractions;

namespace BookSpace.UnitTests.Jobs;

// Hardening pass, 2026-10 (finding 1). A distributed lease's expiry cannot be
// judged against any one caller's clock — see IJobLeaseRepository's own
// header for the reasoning. Encoded here as a permanent, compiler-checked
// guarantee rather than a runtime behavior that could quietly regress: if a
// future change "helpfully" reintroduces a caller-supplied `DateTime` — to
// make a call site easier to unit test, say — this fails immediately and
// points back at why it was removed, rather than shipping a real clock-skew
// bug that only a multi-instance deployment would ever surface.
public class JobLeaseRepositoryContractTests
{
    [Fact]
    public void TryAcquireOrRenewAsync_TakesNoCallerSuppliedTime()
    {
        var method = typeof(IJobLeaseRepository).GetMethod(nameof(IJobLeaseRepository.TryAcquireOrRenewAsync))!;

        Assert.DoesNotContain(method.GetParameters(), p => p.ParameterType == typeof(DateTime));
    }

    [Fact]
    public void ReleaseAsync_TakesNoCallerSuppliedTime()
    {
        var method = typeof(IJobLeaseRepository).GetMethod(nameof(IJobLeaseRepository.ReleaseAsync))!;

        Assert.DoesNotContain(method.GetParameters(), p => p.ParameterType == typeof(DateTime));
    }
}
