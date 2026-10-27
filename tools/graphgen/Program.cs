using System.Globalization;

using GraphGen;

// graphgen: builds a synthetic follow graph with power-law degrees in Recommendations' PostgreSQL
// schema, then times each query shape for a sample of readers. Every result is a local measurement:
// it prints its setup with it.
//   dotnet run --project tools/graphgen -c Release -- --connection "<owner connection string>"
//       [--accounts 100000] [--mean-following 30] [--exponent 2.1] [--seed 42] [--readers 100] [--timeout-ms 5000]
var options = Options.Parse(args);
Console.WriteLine($"graphgen: {options}");

var graph = Generator.Build(options);
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"generated {graph.Accounts:N0} accounts, {graph.Edges.Count:N0} follows; max followers {graph.MaxFollowers:N0}, " +
    $"accounts with >= 10,000 followers: {graph.Supernodes:N0}"));

await Loader.Load(options.Connection, graph);
var report = await Benchmark.Run(options, graph);
Console.WriteLine(report);
