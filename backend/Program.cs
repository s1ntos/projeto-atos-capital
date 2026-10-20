using System.Text;
using backend.Data;
using backend.Data.Repositories;
using backend.Enums;
using backend.Events;
using backend.Models;
using backend.Seguranca;
using backend.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();


builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
});

// DbContext (PostgreSQL)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddRepositories();

// CORS: origens permitidas configuráveis via appsettings (lista vazia por padrão = nada liberado)
var origensPermitidas = builder.Configuration.GetSection("Cors:OrigensPermitidas").Get<string[]>() ?? [];
const string PoliticaCorsFrontend = "PoliticaCorsFrontend";
builder.Services.AddCors(options =>
{
    options.AddPolicy(PoliticaCorsFrontend, policy =>
    {
        policy.WithOrigins(origensPermitidas)
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Serviços da Aplicação
builder.Services.AddScoped<AuthService>();
builder.Services.AddSingleton<IPasswordHasher<Usuario>, PasswordHasher<Usuario>>();
builder.Services.AddSingleton<SenhaService>();
builder.Services.AddLoginRateLimit(builder.Configuration); // limite de tentativas em POST /api/Auth/login
builder.Services.Configure<SeedOptions>(builder.Configuration.GetSection(SeedOptions.Secao));

// Atrás de proxy reverso (Coolify/Traefik) o IP do cliente vem em X-Forwarded-For.
// Só habilitar quando a API não é alcançável sem passar pelo proxy; ForwardLimit=1 usa apenas o último salto.
if (builder.Configuration.GetValue<bool>("ForwardedHeaders:Habilitado"))
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    });
}
builder.Services.AddScoped<RelatorioService>();
builder.Services.AddEventosWebhooks(builder.Configuration); // RF06: Channel + EventoService + WebhookDispatcher (Events/)
builder.Services.AddAnexos(builder.Configuration);          // RF11: AnexosOptions + ArquivoService (uploads NÃO são static files)

// RF07/RF09 — Monitor de SLA (BackgroundService) + options da seção "Sla"
builder.Services.Configure<SlaOptions>(builder.Configuration.GetSection(SlaOptions.Secao));
builder.Services.AddSingleton<SlaMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SlaMonitorService>());

// Configuração da Autenticação via JWT Bearer
var jwtKey = builder.Configuration["Jwt:Key"]!;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

// Configuração do Swagger com suporte a Authorize (Bearer Token)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Atos Support API", Version = "v1" });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "Insira o token JWT neste formato: Bearer {seu_token}",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

if (app.Configuration.GetValue<bool>("ForwardedHeaders:Habilitado"))
{
    app.UseForwardedHeaders();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Atos Support API v1");
        c.RoutePrefix = "swagger";
    });
}

app.UseHttpsRedirection();

// CORS precisa vir antes da autenticação/autorização
app.UseCors(PoliticaCorsFrontend);
app.UseRateLimiter();

// Ativa Autenticação e Autorização
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Carga Inicial de Dados (Seed Data)
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    // Aplica as migrations criando o banco de dados e as tabelas caso não existam
    context.Database.Migrate();

    var gruposEmpresas = scope.ServiceProvider.GetRequiredService<IBaseRepository<GrupoEmpresa>>();
    var usuarios = scope.ServiceProvider.GetRequiredService<IBaseRepository<Usuario>>();
    var slaCategorias = scope.ServiceProvider.GetRequiredService<IBaseRepository<SLACategoria>>();

    // Garante a existência do grupo (empresa matriz Atos Capital)
    var grupo = gruposEmpresas.ObterTodos().FirstOrDefault(g => g.Nome == "Atos Capital");
    if (grupo == null)
    {
        grupo = new GrupoEmpresa
        {
            Nome = "Atos Capital",
            IdExterno = "GRP-ATOS-001",
            Tipo = TipoGrupoEnum.MATRIZ
        };
        gruposEmpresas.Add(grupo);
        gruposEmpresas.SalvarAlteracoes();
    }

    // Garante a existência de uma segunda empresa cliente, para testar isolamento de dados entre clientes
    var grupoNortec = gruposEmpresas.ObterTodos().FirstOrDefault(g => g.Nome == "Cliente Nortec");
    if (grupoNortec == null)
    {
        grupoNortec = new GrupoEmpresa
        {
            Nome = "Cliente Nortec",
            IdExterno = "GRP-NORTEC-001",
            Tipo = TipoGrupoEnum.MATRIZ
        };
        gruposEmpresas.Add(grupoNortec);
        gruposEmpresas.SalvarAlteracoes();
    }

    // Senhas: remove qualquer valor em texto puro deixado por seeds antigos (esses usuários ficam só com SSO
    // até receberem senha nova), cria os usuários de teste apenas quando Seed:UsuariosDeTeste = true
    // (desenvolvimento) e o admin inicial de produção a partir de Seed:AdminEmail/AdminSenha.
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Seed");
    var senhas = scope.ServiceProvider.GetRequiredService<SenhaService>();
    var seedOptions = app.Configuration.GetSection(SeedOptions.Secao).Get<SeedOptions>() ?? new SeedOptions();

    UsuarioSeeder.ZerarSenhasEmTextoPuro(usuarios, logger);
    if (seedOptions.UsuariosDeTeste)
    {
        UsuarioSeeder.SeedUsuariosDeTeste(usuarios, senhas, seedOptions, grupo.Id, grupoNortec.Id, logger);
    }
    UsuarioSeeder.SeedAdminInicial(usuarios, senhas, seedOptions, grupo.Id, logger);

    // RF07 — Regras de SLA padrão (idempotente): Plataforma x Acesso/Erro/Dúvida x 4 prioridades
    DataSeeder.SeedSlaCategorias(slaCategorias);
}

app.Run();
