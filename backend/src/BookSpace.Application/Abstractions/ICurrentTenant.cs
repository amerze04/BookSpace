namespace BookSpace.Application.Abstractions;

// CLAUDE.md §4.2: the tenant read-side used by BookSpaceDbContext's global
// query filters, SaveChanges* validation, and the RLS connection interceptor.
// Null means no tenant context — a SysAdmin (see docs/decisions/0009), or
// code running outside an HTTP request (SeedData, migrations). Implemented in
// BookSpace.Api (BookSpace.Infrastructure has no ASP.NET Core dependency and
// only needs this interface, which it already reaches via its reference to
// BookSpace.Application).
public interface ICurrentTenant
{
    Guid? OrgId { get; }
}
