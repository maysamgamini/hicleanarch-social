using System.Runtime.CompilerServices;

// The integration tests open the module's own context to prove what it stores.
[assembly: InternalsVisibleTo("Integration.Tests")]
