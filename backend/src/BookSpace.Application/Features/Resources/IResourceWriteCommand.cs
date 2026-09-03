using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Resources;

// The fields a create and an edit have in common — which, because an edit is a
// full representation (docs/decisions/0015-api-contract-and-pagination.md), is
// all of them except the id.
//
// It exists so ResourceFieldRules can validate both commands from one place,
// the same way PagedQueryRules serves every list query. Note the validators
// still have to be typed against the *concrete* command (CLAUDE.md §12's
// discovery gotcha): IValidator<T> is invariant, so nothing typed against this
// interface would ever be resolved.
public interface IResourceWriteCommand
{
    string Name { get; }

    string? Description { get; }

    ResourceType ResourceType { get; }

    int Capacity { get; }

    string TimeZoneId { get; }

    bool RequiresApproval { get; }

    int? MinDurationMinutes { get; }

    int? MaxDurationMinutes { get; }
}
