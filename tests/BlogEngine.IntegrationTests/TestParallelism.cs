// Each collection starts a real PostgreSQL container. Bound fixture startup on high-core hosts;
// scenario-level barriers still run all 100 concurrent requests across separate hosts/connections.
[assembly: Xunit.v3.Parallelization(MaxThreads = 4)]
