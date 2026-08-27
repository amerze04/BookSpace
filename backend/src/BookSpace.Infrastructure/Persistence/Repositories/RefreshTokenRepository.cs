using BookSpace.Application.Abstractions;
using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence.Repositories;

internal sealed class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly BookSpaceDbContext _context;

    public RefreshTokenRepository(BookSpaceDbContext context)
    {
        _context = context;
    }

    // Indexed lookup against UQ_RefreshTokens_TokenHash — the reason the stored
    // hash has to be deterministic (see RefreshTokenFactory).
    public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        _context.RefreshTokens.FirstOrDefaultAsync(r => r.TokenHash == tokenHash, cancellationToken);

    public void Add(RefreshToken token) => _context.RefreshTokens.Add(token);

    // FR-2.2. Loads the family (IX_RefreshTokens_Family covers this) and revokes
    // through the domain method rather than an ExecuteUpdate, so the change is
    // tracked and lands in the caller's single SaveChangesAsync alongside
    // everything else in the unit of work.
    public async Task RevokeFamilyAsync(Guid familyId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var active = await _context.RefreshTokens
            .Where(r => r.FamilyId == familyId && r.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var token in active)
        {
            token.Revoke(nowUtc);
        }
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _context.SaveChangesAsync(cancellationToken);
}
