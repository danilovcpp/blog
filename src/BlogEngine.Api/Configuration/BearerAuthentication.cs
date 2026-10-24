using BlogEngine.Api.Diagnostics;
using BlogEngine.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BlogEngine.Api.Configuration;

public static class BearerAuthentication
{
    public static void AddBearerAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<IdentityOptions>, BearerKeyValidation, IHttpContextAccessor>((options, identity, validation, http) =>
            {
                options.MapInboundClaims = false;
                options.SaveToken = false;
                options.IncludeErrorDetails = false;
                options.RefreshOnIssuerKeyNotFound = false;
                options.TokenHandlers.Clear();
                options.TokenHandlers.Add(new AsyncBearerHandler(validation, http));
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = identity.Value.Issuer,
                    ValidAudience = identity.Value.Audience,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ValidTypes = ["JWT"],
                    IgnoreTrailingSlashWhenValidatingAudience = false,
                    TryAllIssuerSigningKeys = false,
                    LogTokenId = false,
                    IncludeTokenOnFailedValidation = false,
                    AuthenticationType = JwtBearerDefaults.AuthenticationScheme,
                    NameClaimType = "sub",
                    RoleClaimType = "__no_external_roles",
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var authorization = context.Request.Headers.Authorization;
                        if (authorization.Count != 0 && (authorization.Count != 1
                            || !authorization[0]!.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                            || string.IsNullOrWhiteSpace(authorization[0]![7..])))
                        {
                            context.Fail("Invalid access token.");
                        }
                        return Task.CompletedTask;
                    },
                    OnAuthenticationFailed = context =>
                    {
                        if (context.Exception is IdentityKeysUnavailableException)
                        {
                            context.HttpContext.Items[typeof(IdentityKeysUnavailableException)] = true;
                        }
                        return Task.CompletedTask;
                    },
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        var unavailable = context.HttpContext.Items.ContainsKey(typeof(IdentityKeysUnavailableException));
                        var code = unavailable ? "identity_keys_unavailable" : context.Request.Headers.ContainsKey("Authorization")
                            ? "invalid_token" : "authentication_required";
                        await ProblemDetailsHandler.WriteAsync(context.HttpContext, unavailable ? 503 : 401, code);
                    },
                    OnForbidden = context => ProblemDetailsHandler.WriteAsync(context.HttpContext, 403, "forbidden"),
                };
            });
        services.AddAuthorizationBuilder().SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser().Build());
    }

    private sealed class AsyncBearerHandler(BearerKeyValidation validation, IHttpContextAccessor http) : TokenHandler
    {
        public override Task<TokenValidationResult> ValidateTokenAsync(string token, TokenValidationParameters parameters) =>
            validation.ValidateAsync(token, parameters, http.HttpContext?.RequestAborted ?? CancellationToken.None);
    }
}
