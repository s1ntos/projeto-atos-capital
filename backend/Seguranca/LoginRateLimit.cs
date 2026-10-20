using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace backend.Seguranca;

/// <summary>Opções da seção "Auth:Login" do appsettings.</summary>
public sealed class LoginOptions
{
    public const string Secao = "Auth:Login";

    /// <summary>Tentativas de login aceitas por IP dentro da janela. Acima disso a API responde 429.</summary>
    public int MaxTentativasPorJanela { get; set; } = 10;

    public int JanelaSegundos { get; set; } = 60;
}

public static class LoginRateLimit
{
    public const string Politica = "login";

    /// <summary>
    /// Limita POST /api/Auth/login por IP (janela fixa). Atrás de proxy, o IP real depende de
    /// ForwardedHeaders estar habilitado (seção "ForwardedHeaders" do appsettings).
    /// </summary>
    public static IServiceCollection AddLoginRateLimit(this IServiceCollection services, IConfiguration configuration)
    {
        var opcoes = configuration.GetSection(LoginOptions.Secao).Get<LoginOptions>() ?? new LoginOptions();
        var limite = Math.Max(1, opcoes.MaxTentativasPorJanela);
        var janela = TimeSpan.FromSeconds(Math.Max(1, opcoes.JanelaSegundos));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (contexto, cancelamento) =>
            {
                contexto.HttpContext.Response.ContentType = "application/json";
                await contexto.HttpContext.Response.WriteAsync(
                    "\"Muitas tentativas de login. Aguarde um minuto e tente novamente.\"", cancelamento);
            };
            options.AddPolicy(Politica, contexto =>
            {
                var chave = contexto.Connection.RemoteIpAddress?.ToString() ?? "desconhecido";
                return RateLimitPartition.GetFixedWindowLimiter(chave, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limite,
                    Window = janela,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });
        });

        return services;
    }
}
