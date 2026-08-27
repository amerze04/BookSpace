using BookSpace.Domain.Entities;

namespace BookSpace.Application.Abstractions;

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken);

    void Add(RefreshToken token);

    // FR-2.2: revokes every still-active token in the family. Loads the rows so
    // the revocation goes through RefreshToken.Revoke and lands in the same
    // SaveChangesAsync as everything else in the unit of work.
    Task RevokeFamilyAsync(Guid familyId, DateTime nowUtc, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
