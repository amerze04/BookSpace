using System.Runtime.CompilerServices;

// Same convention as BookSpace.Infrastructure: HttpContextCurrentTenant is
// internal because nothing outside Api should construct it, but its claim
// -reading behavior is exactly what needs direct unit testing.
[assembly: InternalsVisibleTo("BookSpace.UnitTests")]
