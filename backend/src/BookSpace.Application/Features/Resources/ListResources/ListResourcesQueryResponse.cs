namespace BookSpace.Application.Features.Resources.ListResources;

// One row of GET /resources. A summary, not the whole aggregate: description,
// duration limits and audit timestamps are on the detail response instead, so
// a list of forty resources isn't forty copies of a thousand-character
// description.
//
// IsArchived is on the wire because FR-3.5 keeps archived resources readable —
// a client asking for includeArchived=true needs to be able to tell which rows
// came back because of it.
public sealed record ListResourcesQueryResponse(
    Guid Id,
    string Name,
    string ResourceType,
    int Capacity,
    string TimeZoneId,
    bool RequiresApproval,
    bool IsArchived);
