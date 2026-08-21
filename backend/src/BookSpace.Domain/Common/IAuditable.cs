namespace BookSpace.Domain.Common;

// Shape backing the CreatedAtUtc/CreatedByUserId/UpdatedAtUtc/UpdatedByUserId
// convention documented in docs/bookspace-schema-v2.sql. UpdatedByUserId is
// nullable because some transitions are system-initiated (e.g. the no-show
// release job), not made by a person.
public interface IAuditable
{
    DateTime CreatedAtUtc { get; }
    Guid CreatedByUserId { get; }
    DateTime UpdatedAtUtc { get; }
    Guid? UpdatedByUserId { get; }
}
