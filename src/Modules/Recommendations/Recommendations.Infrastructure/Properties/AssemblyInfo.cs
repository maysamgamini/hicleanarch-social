using System.Runtime.CompilerServices;

// The integration tests open the module's own context; the graph tests run the adapters' contract suite.
[assembly: InternalsVisibleTo("Integration.Tests")]
[assembly: InternalsVisibleTo("Graph.Tests")]

// The benchmark runs the adapter's own query text and migrator.
[assembly: InternalsVisibleTo("GraphGen")]
