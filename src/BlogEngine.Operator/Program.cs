using BlogEngine.Operator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Logging.ClearProviders();
OperatorOptions.Register(builder.Services, builder.Configuration);
using var host = builder.Build();
try { _ = host.Services.GetRequiredService<IOptions<OperatorOptions>>().Value; }
catch (OptionsValidationException)
{
    Console.Error.WriteLine("{\"code\":\"invalid_configuration\",\"message\":\"Operator configuration is missing or invalid.\"}");
    return 5;
}
Console.Error.WriteLine("{\"code\":\"invalid_arguments\",\"message\":\"Unknown command.\"}");
return 2;
