using Xunit;

// Sequential on purpose: each test class's factory starts its own Azurite and publishes its
// connection string through PROCESS-WIDE environment variables (Program.cs reads storage settings
// before the factory's in-memory config applies). Run in parallel, one class's host could pick up
// another class's container and get 500s once that class finished and removed it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
[assembly: TestFramework("PoSeeReview.Integration.TestAssemblyFixture", "PoSeeReview.Integration")]

namespace PoSeeReview.Integration;

public class TestAssemblyFixture : Xunit.Sdk.XunitTestFramework
{
    public TestAssemblyFixture(Xunit.Abstractions.IMessageSink messageSink)
        : base(messageSink)
    {
        // Set environment variable BEFORE any tests run to disable Serilog
        Environment.SetEnvironmentVariable("DISABLE_SERILOG", "true");
    }
}
