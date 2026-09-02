namespace BookSpace.Application.Features.Resources.ReplaceAvailabilityWindows;

// The 200 body of PUT /resources/{id}/availability-windows.
//
// Returns the schedule rather than 204, for the reason the archive endpoint
// returns a body too: the server assigned an id to every row, and normalized the
// order. A client that got 204 would have to re-read to learn either.
//
// ResourceId is echoed so the body stands on its own in a log or a test fixture
// without the URL that produced it.
public sealed record ReplaceAvailabilityWindowsCommandResponse(
    Guid ResourceId,
    IReadOnlyList<ReplacedAvailabilityWindow> AvailabilityWindows);
