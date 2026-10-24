using BlogEngine.Core.Accounts.Application;
using BlogEngine.Core.Accounts.Contracts;
using BlogEngine.Core.Shared;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.Infrastructure.Persistence.Accounts;
using BlogEngine.Operator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var correlationId = Guid.NewGuid();
string? errorCode = null;
var exitCode = 0;
var configurationValidated = false;
object? result = null;
try
{
    // Command arguments are parsed separately and cannot override protected host configuration.
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
    builder.Logging.ClearProviders();
    OperatorOptions.Register(builder.Services, builder.Configuration);
    builder.Services.AddDbContext<BlogDbContext>((provider, options) => options.UseNpgsql(
        provider.GetRequiredService<IOptions<OperatorOptions>>().Value.ConnectionString, npgsql => npgsql.CommandTimeout(5)));
    builder.Services.AddScoped<IRoleAdministrationStore, RoleAdministrationStore>();
    builder.Services.AddScoped(provider => new RoleAdministration(provider.GetRequiredService<IRoleAdministrationStore>(), provider.GetRequiredService<IOptions<OperatorOptions>>().Value.Issuer));
    using var host = builder.Build();
    var options = host.Services.GetRequiredService<IOptions<OperatorOptions>>().Value;
    configurationValidated = true;
    var command = CommandParser.Parse(args, options.Issuer);
    await host.StartAsync();
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
    timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
    await using var scope = host.Services.CreateAsyncScope();
    var administration = scope.ServiceProvider.GetRequiredService<RoleAdministration>();
    if (command.Kind == OperatorCommandKind.Find)
    {
        result = await administration.FindAsync(new(options.Issuer, command.Id), timeout.Token) ?? throw new DomainError("user_not_found");
    }
    else if (command.Kind == OperatorCommandKind.List)
    {
        var user = await administration.ListAsync(command.Id, timeout.Token);
        result = new { user.UserId, user.Roles };
    }
    else
    {
        var actor = OperatingSystem.IsWindows() ? Environment.UserDomainName + "\\" + Environment.UserName : Environment.UserName;
        result = await administration.ChangeAsync(new(command.Id, command.Role!.Value,
            command.Kind == OperatorCommandKind.Grant ? RoleChangeAction.Grant : RoleChangeAction.Revoke, command.Reason!, actor, correlationId), timeout.Token);
    }
}
catch (DomainError error)
{
    (exitCode, errorCode) = error.Code == "user_not_found" ? (3, "user_not_found") : (2, "invalid_arguments");
}
catch (OptionsValidationException) { (exitCode, errorCode) = (5, "invalid_configuration"); }
catch (Exception error) when (!configurationValidated && error is not OutOfMemoryException)
{
    (exitCode, errorCode) = (5, "invalid_configuration");
}
catch (Exception error) when (error is FormatException or InvalidDataException or ArgumentException)
{
    (exitCode, errorCode) = (5, "invalid_configuration");
}
catch (OperationCanceledException) { (exitCode, errorCode) = (130, "cancelled"); }
catch (RoleAdministrationException error) { (exitCode, errorCode) = (4, error.Code); }
catch (Exception error) when (error is not OutOfMemoryException)
{
    (exitCode, errorCode) = (4, "database_error");
}
// Output failures must not be misreported as a rolled-back database transaction after a confirmed commit.
if (errorCode is not null) { await CommandResultWriter.WriteErrorAsync(Console.Error, errorCode, correlationId); }
else { await CommandResultWriter.WriteSuccessAsync(Console.Out, result!); }
return exitCode;
