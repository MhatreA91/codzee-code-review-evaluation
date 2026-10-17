using Xunit;

// Serial execution: one integration test toggles ASPNETCORE_HTTPS_PORT for its host build.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

