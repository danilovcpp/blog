using Microsoft.Extensions.Hosting;

using var host = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
}).Build();
Console.Error.WriteLine("Operator commands are not available in the Setup phase. See tasks.md, US3.");
return 2;
