namespace BlogEngine.IntegrationTests.Fixtures;

// These tests restart Garage and stream/hash hundreds of MiB. Isolate that host load
// from other collections' 100-way database/authentication scenarios.
// Concurrency inside each scenario is unchanged.
[CollectionDefinition("Media transfers", DisableParallelization = true)]
public sealed class MediaTransferCollection;
