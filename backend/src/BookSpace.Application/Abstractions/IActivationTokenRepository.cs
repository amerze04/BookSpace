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

    // Hardening pass, 2026-09-25 (finding 3) — every token for this user that
    // ReissueInvitationCommandRequestHandler must supersede before adding a new
    // one, so the account never has two simultaneously usable credentials
    // outstanding. "Still live" means CanBeRedeemed at the moment of the call —
    // not consumed, not already superseded, not expired — so an already-spent
    // or already-expired row is left alone rather than touched for no reason.
    Task<IReadOnlyList<ActivationToken>> FindRedeemableForUserAsync(
        Guid userId, DateTime nowUtc, CancellationToken cancellationToken);

    // Hardening pass, 2026-09-25 (finding 3). Whether this account has ever
    // completed activation — the only way User.SetPassword is ever called (see
    // ActivateAccountCommandRequestHandler) is by consuming a token, so this is
    // what "has this account already been activated" actually means. There is
    // no separate flag on User for it to drift from.
    Task<bool> HasEverBeenConsumedAsync(Guid userId, CancellationToken cancellationToken);

    void Add(ActivationToken token);
}
