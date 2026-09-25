using BookSpace.Application.Abstractions;
using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence.Repositories;

// No IgnoreQueryFilters and no TenantBypassScope here, and for once that is not
// discipline — ActivationTokens has no OrgId, no query filter and no row-level
// security predicate, so there is nothing to bypass. See the entity for why.
//
// No SaveChangesAsync either: the activation write spans this table and Users,
// and has to land in one transaction, so the caller saves through
// IAuthenticationUserRepository.SaveChangesUnfilteredAsync. Both repositories
// hold the same scoped DbContext, so that one call persists changes tracked
// here too.
internal sealed class ActivationTokenRepository : IActivationTokenRepository
{
    private readonly BookSpaceDbContext _context;

    public ActivationTokenRepository(BookSpaceDbContext context)
    {
        _context = context;
    }

    // Indexed lookup against UQ_ActivationTokens_TokenHash — the reason the
    // stored hash has to be deterministic (see SecureToken).
    public Task<ActivationToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        _context.ActivationTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

    // Tracked, not AsNoTracking: the caller (ReissueInvitationCommandRequestHandler)
    // calls Supersede() on each and needs the change tracked. Uses
    // IX_ActivationTokens_User, the index this table's own configuration
    // comment already anticipated needing once reissuing existed.
    public async Task<IReadOnlyList<ActivationToken>> FindRedeemableForUserAsync(
        Guid userId,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        await _context.ActivationTokens
            .Where(t => t.UserId == userId
                && t.ConsumedAtUtc == null
                && t.SupersededAtUtc == null
                && t.ExpiresAtUtc > nowUtc)
            .ToListAsync(cancellationToken);

    public Task<bool> HasEverBeenConsumedAsync(Guid userId, CancellationToken cancellationToken) =>
        _context.ActivationTokens
            .AsNoTracking()
            .AnyAsync(t => t.UserId == userId && t.ConsumedAtUtc != null, cancellationToken);

    public void Add(ActivationToken token) => _context.ActivationTokens.Add(token);
}
