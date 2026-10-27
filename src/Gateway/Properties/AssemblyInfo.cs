using System.Runtime.CompilerServices;

// The integration tests start the gateway in memory in front of the monolith's own test host.
[assembly: InternalsVisibleTo("Integration.Tests")]
