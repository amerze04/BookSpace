namespace BookSpace.Application.Abstractions;

// FR-2.3: the concrete algorithm lives in Infrastructure so the Identity
// package never reaches this layer (CLAUDE.md §3).
public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string passwordHash, string password);
}
