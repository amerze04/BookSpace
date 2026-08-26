namespace BookSpace.Infrastructure.Security;

// Claim names fixed by docs/decisions/0009-jwt-claims-and-token-lifetimes.md.
// Constants rather than literals because Phase 4's tenant accessor reads OrgId
// back out and the two sides must not drift.
public static class BookSpaceClaims
{
    // Custom claim. Absent entirely for a SysAdmin, who has no OrgId — an
    // absent claim cannot be misread as a real tenant the way "" or an empty
    // Guid could.
    public const string OrgId = "orgId";
}
