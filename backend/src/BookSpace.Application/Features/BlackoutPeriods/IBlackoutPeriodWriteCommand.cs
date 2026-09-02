namespace BookSpace.Application.Features.BlackoutPeriods;

// The fields a create and an edit have in common — which, because an edit is a
// full representation (docs/decisions/0015), is all of them except the ids.
//
// It exists so BlackoutPeriodFieldRules can validate both commands from one
// place, the same way IResourceWriteCommand serves the resource pair. Note the
// validators still have to be typed against the *concrete* command
// (CLAUDE.md §12's discovery gotcha): IValidator<T> is invariant, so nothing
// typed against this interface would ever be resolved.
public interface IBlackoutPeriodWriteCommand
{
    DateTime StartsAtUtc { get; }

    DateTime EndsAtUtc { get; }

    string? Reason { get; }
}
