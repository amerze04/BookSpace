using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Messaging;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Resources.ListResources;

// FR-3.1 / FR-3.5. Paged per docs/decisions/0015: defaults live on the
// constructor parameters, so a client that sends no query string gets page 1 of
// 20 with no handler-side normalization.
//
// IncludeArchived defaults to false because FR-3.5 archives rather than deletes:
// without it, every member browsing for something to book would see resources
// that exist only to preserve their booking history. Opting in is how an admin
// reviews them.
// Type became filterable on 2026-09-04, when it became an enum. Until then it
// was sortable but not filterable — you could order a catalogue by type but not
// narrow it to one, which is an odd shape for a browse endpoint and part of why
// the column felt inert. Filtering on free text would have been the wrong fix
// anyway: "Room" and "room" were different values, so a filter could not have
// been trusted to return everything.
//
// Optional, and null means every type. Unlike IncludeArchived there is no
// sensible default subset to hide.
//
// Search and RequiresApproval added 2026-09-15, for the WP-7 browse screen's
// search box and "Approval" filter: both were designed against this endpoint
// before it could actually answer them, and client-side filtering over one
// fetched page was the accepted stopgap (docs/wp7-plan.md) only until this
// landed. Search matches Name or Description, case sensitivity following
// whatever the database's collation already does for LIKE — the same
// non-decision this codebase makes everywhere else a string is compared, not
// a new one. Null means "no text filter", exactly like Type meaning "every
// type"; an empty string is treated as null too; see the repository.
//
// RequiresApproval is a nullable bool, not a bool defaulting to false, because
// unlike IncludeArchived there is no sensible default subset to hide — a
// member browsing wants to see both kinds of resource unless they ask
// otherwise.
public sealed record ListResourcesQueryRequest(
    int Page = PagingDefaults.Page,
    int PageSize = PagingDefaults.PageSize,
    string? Sort = null,
    bool IncludeArchived = false,
    ResourceType? Type = null,
    string? Search = null,
    bool? RequiresApproval = null)
    : IRequest<PagedResult<ListResourcesQueryResponse>>, IPagedQuery;
