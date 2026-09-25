namespace BookSpace.Application.Features.Authentication;

// What this application will accept as a password.
//
// **This is the first place BookSpace ever sets one**, so the policy had to be
// invented here — FR-2.3 says passwords are hashed and never stored in
// plaintext, and neither it nor anything else in the PRD says how long or how
// complex one must be. Recorded as a deliberate choice rather than left as
// magic numbers in a validator, and flagged to the owner rather than presented
// as a requirement (docs/user-management-plan.md §5, phase 2).
//
// Length only, no composition rules — no "must contain an uppercase letter and
// a digit". That follows NIST SP 800-63B, which dropped composition rules
// because they push people towards predictable substitutions (Password1!) while
// doing little for entropy; length is what actually helps. 12 is the shortest
// that is defensible today for a credential with no second factor.
//
// The maximum is not cosmetic. IPasswordHasher runs PBKDF2 at 100k iterations
// over whatever it is given, so an unbounded password is a cheap way to make
// the server do unbounded work on an anonymous endpoint.
//
// One place, because the rule has to be the same everywhere a password is set.
// Today that is activation; a password reset or a self-service change (both out
// of scope, §3.4) would be the next callers, and a second copy is how they
// would come to disagree.
public static class PasswordPolicy
{
    public const int MinimumLength = 12;

    public const int MaximumLength = 128;
}
