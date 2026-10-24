var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

await app.RunAsync();

// WebApplicationFactory needs a public entry point; endpoints belong to later phases.
public partial class Program;
