using BookSpace.Domain.Entities;

namespace BookSpace.Application.Abstractions;

// ActivationTokens carries no OrgId and is not tenant-filtered (see the entity),
// so unlike every other repository in this folder there is nothing here to
// bypass and nothing to be scoped by — the token's own secrecy is the access
// control. Saving is deliberately absent: the activation write spans this table
// and Users, and it has to land in one SaveChangesAsync, so the caller uses
// IAuthenticationUserRepository.SaveChangesUnfilteredAsync for both.
public interface IActivationTokenRepository
{
    // Indexed lookup against UQ_ActivationTokens_TokenHash. Returns a spent or
    // expired token rather than filtering it out: the handler needs to see it
    // to consume it or to refuse, and it must refuse identically whether the
    // token was spent, expired, or never existed
    // (docs/user-management-plan.md §4.2).
    Task<ActivationToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken);

    void Add(ActivationToken token);
}
