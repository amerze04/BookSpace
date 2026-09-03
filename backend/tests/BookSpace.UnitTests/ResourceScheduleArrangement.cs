using BookSpace.Domain.Entities;

namespace BookSpace.UnitTests;

// Arrangement only, for the tests that need a resource with a window or two
// before they exercise something else — a query filter, the tenant guard, a
// timezone change.
//
// It exists because WP-3 Phase 5 step 4 deleted Resource.AddAvailabilityWindow.
// The API has gone exclusively through ReplaceAvailabilityWindows since Phase 3,
// and the per-window add carried a live EF trap (a window added to an
// already-tracked resource is marked Modified, saves as a zero-row UPDATE, and
// surfaces as a 409 for what is plainly an insert — see
// IResourceRepository.AddAvailabilityWindows). Leaving one method that sets a
// schedule is the point of the cleanup, so this appends through that method
// rather than reintroducing the other one.
//
// A *test* helper deliberately, not a domain method: appending is not a
// behaviour the application has or wants, and a production method with these
// semantics would be the trap all over again. Tests that are actually about the
// weekly schedule call ReplaceAvailabilityWindows directly — see
// AvailabilityWindowTests.
internal static class ResourceScheduleArrangement
{
    // Same parameter order the deleted method had, so a call site reads
    // unchanged. Returns the window it added, which two of the tenant-isolation
    // tests need in order to track it on its own.
    public static AvailabilityWindow AddWindow(
        this Resource resource,
        Guid availabilityWindowId,
        DayOfWeek weekday,
        TimeOnly opensAt,
        TimeOnly closesAt,
        Guid actorUserId,
        DateTime nowUtc)
    {
        var replacement = resource.AvailabilityWindows
            .Select(w => new AvailabilityWindowDefinition(w.Id, w.Weekday, w.OpensAt, w.ClosesAt))
            .Append(new AvailabilityWindowDefinition(availabilityWindowId, weekday, opensAt, closesAt))
            .ToList();

        // ReplaceAvailabilityWindows returns the collection in the order it was
        // given, so the appended window is the last one.
        return resource.ReplaceAvailabilityWindows(replacement, actorUserId, nowUtc).Last();
    }
}
