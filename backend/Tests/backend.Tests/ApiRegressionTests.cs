using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using backend.Controllers;
using backend.Data;
using backend.Data.Repositories;
using backend.DTOs;
using backend.Enums;
using backend.Events;
using backend.Models;
using backend.Seguranca;
using backend.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Executa os controllers e serviços reais por HTTP, com JWT real e dados sintéticos.
/// O host de teste usa SQLite e captura eventos; não executa o bootstrap PostgreSQL de Program.cs.
/// </summary>
public sealed class ApiRegressionTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly string _uploads = Path.Combine(Path.GetTempPath(), "backend-api-tests-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    private const string SenhaTeste = "Senha-Teste-123";
    private const int LimiteLoginPorMinuto = 30;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = key,
            ["Jwt:Issuer"] = "testes",
            ["Jwt:Audience"] = "testes",
            ["Anexos:Diretorio"] = _uploads,
            ["Auth:Login:MaxTentativasPorJanela"] = LimiteLoginPorMinuto.ToString(),
            ["Auth:Login:JanelaSegundos"] = "60"
        });
        builder.Services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly)
            .AddJsonOptions(options => options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles);
        builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
        builder.Services.AddRepositories();
        builder.Services.AddScoped<AuthService>();
        builder.Services.AddSingleton<IPasswordHasher<Usuario>, PasswordHasher<Usuario>>();
        builder.Services.AddSingleton<SenhaService>();
        builder.Services.AddLoginRateLimit(builder.Configuration);
        builder.Services.AddScoped<RelatorioService>();
        builder.Services.AddAnexos(builder.Configuration);
        builder.Services.Configure<SlaOptions>(builder.Configuration.GetSection(SlaOptions.Secao));
        builder.Services.AddSingleton<SlaMonitorService>();
        builder.Services.AddSingleton<IEventoService, EventosEmMemoria>();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
                ValidateIssuerSigningKey = true, ValidIssuer = "testes", ValidAudience = "testes",
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key))
            });
        _app = builder.Build();
        _app.UseRateLimiter();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapControllers();
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            var grupo = new GrupoEmpresa { Nome = "Grupo A" };
            var senhas = scope.ServiceProvider.GetRequiredService<SenhaService>();
            Usuario ComSenha(Usuario u) { u.SenhaHash = senhas.GerarHash(u, SenhaTeste); return u; }
            db.Usuarios.AddRange(
                ComSenha(new Usuario { Email = "cliente@test.invalid", Nome = "Cliente", Papel = PapelEnum.CLIENTE, GrupoEmpresa = grupo }),
                ComSenha(new Usuario { Email = "admin@test.invalid", Nome = "Admin", Papel = PapelEnum.ADMIN, GrupoEmpresa = grupo }),
                ComSenha(new Usuario { Email = "agente@test.invalid", Nome = "Agente", Papel = PapelEnum.AGENTE, GrupoEmpresa = grupo }),
                ComSenha(new Usuario { Email = "outro@test.invalid", Nome = "Outro", Papel = PapelEnum.CLIENTE, GrupoEmpresa = new GrupoEmpresa { Nome = "Grupo B" } }),
                // Usuário vindo do SSO: sem senha, não pode entrar pelo login por senha.
                new Usuario { Email = "sso@test.invalid", Nome = "Sso", Papel = PapelEnum.CLIENTE, GrupoEmpresa = grupo, SenhaHash = string.Empty },
                // Resquício de seed antigo com senha em texto puro: deve ser zerado e nunca aceito.
                new Usuario { Email = "legado@test.invalid", Nome = "Legado", Papel = PapelEnum.CLIENTE, GrupoEmpresa = grupo, SenhaHash = "123456" });
            await db.SaveChangesAsync();
            var repositorioUsuarios = scope.ServiceProvider.GetRequiredService<IBaseRepository<Usuario>>();
            Assert.Equal(1, UsuarioSeeder.ZerarSenhasEmTextoPuro(repositorioUsuarios, NullLogger.Instance));
            Assert.Equal(0, UsuarioSeeder.ZerarSenhasEmTextoPuro(repositorioUsuarios, NullLogger.Instance));
            var sla = scope.ServiceProvider.GetRequiredService<IBaseRepository<SLACategoria>>();
            Assert.Equal(12, DataSeeder.SeedSlaCategorias(sla));
            Assert.Equal(0, DataSeeder.SeedSlaCategorias(sla));
        }
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _http = new HttpClient { BaseAddress = new Uri(address) };
    }

    [Fact]
    public async Task AberturaAtribuicaoResolucaoEFechamentoContinuamFuncionando()
    {
        var id = await CriarChamado();
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var chamado = await db.Chamados.AsNoTracking().SingleAsync(c => c.Id == id);
        Assert.NotNull(chamado.AgenteId);
        Assert.NotNull(chamado.PrazoResolucao);
        Assert.Equal(StatusEnum.EM_ANDAMENTO, chamado.Status);
        Assert.Equal(2, await db.LogsAuditoria.CountAsync(l => l.ChamadoId == id));

        await Login("admin");
        await Exigir(await _http.PatchAsJsonAsync($"/api/Chamados/{id}/status",
            new AlterarStatusChamadoDto(StatusEnum.RESOLVIDO, "Resolvido")), HttpStatusCode.OK);
        await Login("cliente");
        await Exigir(await _http.PatchAsJsonAsync($"/api/Chamados/{id}/status",
            new AlterarStatusChamadoDto(StatusEnum.FECHADO, null)), HttpStatusCode.OK);
        await Login("admin");
        await Exigir(await _http.PatchAsJsonAsync($"/api/Chamados/{id}/status",
            new AlterarStatusChamadoDto(StatusEnum.EM_ANDAMENTO, null)), HttpStatusCode.Conflict);
        var final = await db.Chamados.AsNoTracking().SingleAsync(c => c.Id == id);
        Assert.Equal(StatusEnum.FECHADO, final.Status);
        Assert.NotNull(final.FechadoEm);
        Assert.NotNull(final.ResolvidoEm);
        Assert.Equal(4, await db.LogsAuditoria.CountAsync(l => l.ChamadoId == id));
        var eventos = (EventosEmMemoria)_app.Services.GetRequiredService<IEventoService>();
        Assert.Contains(TiposEvento.ChamadoCriado, eventos.Tipos);
        Assert.Contains(TiposEvento.ChamadoResolvido, eventos.Tipos);
        Assert.Contains(TiposEvento.ChamadoFechado, eventos.Tipos);
    }

    [Fact]
    public async Task AutorizacaoIsolaGruposENotasInternas()
    {
        await Exigir(await _http.GetAsync("/api/Chamados"), HttpStatusCode.Unauthorized);
        await Exigir(await _http.PostAsJsonAsync("/api/Auth/login", new LoginDto("ausente@test.invalid", SenhaTeste)), HttpStatusCode.Unauthorized);
        var id = await CriarChamado();
        await Exigir(await _http.GetAsync("/api/Relatorios/resumo"), HttpStatusCode.Forbidden);
        await Exigir(await _http.PostAsJsonAsync($"/api/Chamados/{id}/interacoes",
            new CriarInteracaoDto("Segredo", TipoInteracaoEnum.NOTA_INTERNA, null)), HttpStatusCode.Forbidden);
        await Login("admin");
        await Exigir(await _http.PostAsJsonAsync($"/api/Chamados/{id}/interacoes",
            new CriarInteracaoDto("Segredo", TipoInteracaoEnum.NOTA_INTERNA, null)), HttpStatusCode.OK);
        await Login("cliente");
        var detalhe = await _http.GetFromJsonAsync<JsonElement>($"/api/Chamados/{id}");
        Assert.Equal(0, detalhe.GetProperty("interacoes").GetArrayLength());
        Assert.Equal(0, detalhe.GetProperty("logsAuditoria").GetArrayLength());
        await Login("outro");
        await Exigir(await _http.GetAsync($"/api/Chamados/{id}"), HttpStatusCode.Forbidden);
        var lista = await _http.GetFromJsonAsync<JsonElement>("/api/Chamados");
        Assert.DoesNotContain(id.ToString(), lista.GetProperty("itens").EnumerateArray().Select(c => c.GetProperty("id").ToString()));
    }

    [Fact]
    public async Task AnexosERelatoriosContinuamDisponiveisComPermissoes()
    {
        var id = await CriarChamado();
        using var form = new MultipartFormDataContent();
        var arquivo = new ByteArrayContent(Encoding.UTF8.GetBytes("conteudo de teste"));
        arquivo.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(arquivo, "arquivos", "exemplo.txt");
        var envio = await _http.PostAsync($"/api/Chamados/{id}/anexos", form);
        await Exigir(envio, HttpStatusCode.Created);
        var json = await envio.Content.ReadFromJsonAsync<JsonElement>();
        var url = json.GetProperty("anexos")[0].GetProperty("url").GetString()!;
        Assert.Equal("conteudo de teste", await _http.GetStringAsync(url));
        await Login("outro");
        await Exigir(await _http.GetAsync(url), HttpStatusCode.Forbidden);
        await Login("admin");
        await Exigir(await _http.GetAsync("/api/Relatorios/resumo"), HttpStatusCode.OK);
        await Exigir(await _http.GetAsync("/api/Relatorios/por-status"), HttpStatusCode.OK);
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Interacoes.CountAsync(i => i.ChamadoId == id && i.Anexos != null));
        Assert.Equal(1, await db.LogsAuditoria.CountAsync(l => l.ChamadoId == id && l.Acao == "ANEXAR_ARQUIVO"));
    }

    [Fact]
    public async Task MonitorSlaGravaAlertasSemDuplicarEPermiteReconhecimento()
    {
        var id = await CriarChamado();
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var chamado = await db.Chamados.SingleAsync(c => c.Id == id);
            chamado.PrazoResolucao = DateTime.UtcNow.AddHours(-1);
            chamado.PrazoResposta = DateTime.UtcNow.AddHours(1);
            await db.SaveChangesAsync();
        }
        await Login("admin");
        await Exigir(await _http.PostAsync("/api/Relatorios/alertas-sla/verificar", null), HttpStatusCode.OK);
        await Exigir(await _http.PostAsync("/api/Relatorios/alertas-sla/verificar", null), HttpStatusCode.OK);
        using var verificacao = _app.Services.CreateScope();
        var context = verificacao.ServiceProvider.GetRequiredService<AppDbContext>();
        var alerta = Assert.Single(await context.AlertasSla.AsNoTracking().ToListAsync());
        Assert.Equal(TipoAlertaSlaEnum.ESTOURO_RESOLUCAO, alerta.Tipo);
        await Exigir(await _http.PatchAsync($"/api/Relatorios/alertas-sla/{alerta.Id}/reconhecer", null), HttpStatusCode.OK);
        Assert.NotNull((await context.AlertasSla.AsNoTracking().SingleAsync()).ReconhecidoEm);
        Assert.Equal(1, await context.LogsAuditoria.CountAsync(l => l.Acao == "ALERTA_SLA_RECONHECIDO"));
    }

    [Fact]
    public async Task RotaMeRetornaUsuarioAutenticadoESemTokenDa401()
    {
        await Exigir(await _http.GetAsync("/api/Auth/me"), HttpStatusCode.Unauthorized);

        await Login("cliente");
        var me = await _http.GetFromJsonAsync<JsonElement>("/api/Auth/me");
        Assert.Equal("cliente@test.invalid", me.GetProperty("email").GetString());
        Assert.Equal("Cliente", me.GetProperty("nome").GetString());
        Assert.Equal((int)PapelEnum.CLIENTE, me.GetProperty("papel").GetInt32());
        Assert.Equal("Grupo A", me.GetProperty("grupoEmpresaNome").GetString());
        Assert.False(me.TryGetProperty("senhaHash", out _));
    }

    [Fact]
    public async Task ResumoEBuscaDeChamadosIsolamPorGrupoEmpresa()
    {
        var id = await CriarChamado(); // cliente (Grupo A), Produto "Plataforma"

        await Login("cliente");
        // CriarChamado atribui automaticamente ao único agente livre ("agente@test.invalid"),
        // então o chamado nasce EM_ANDAMENTO (não ABERTO) — só o total é verificado aqui.
        var resumoCliente = await _http.GetFromJsonAsync<JsonElement>("/api/Chamados/resumo");
        Assert.Equal(1, resumoCliente.GetProperty("total").GetInt32());

        var buscaCliente = await _http.GetFromJsonAsync<JsonElement>("/api/Chamados?busca=plataforma");
        Assert.Equal(1, buscaCliente.GetProperty("total").GetInt32());

        await Login("outro");
        var resumoOutro = await _http.GetFromJsonAsync<JsonElement>("/api/Chamados/resumo");
        Assert.Equal(0, resumoOutro.GetProperty("total").GetInt32());

        var buscaOutro = await _http.GetFromJsonAsync<JsonElement>("/api/Chamados?busca=plataforma");
        Assert.Equal(0, buscaOutro.GetProperty("total").GetInt32());
        Assert.DoesNotContain(id.ToString(), buscaOutro.GetProperty("itens").EnumerateArray().Select(c => c.GetProperty("id").ToString()));
    }

    [Fact]
    public async Task ListaDeAgentesExigePapelDeEquipe()
    {
        await Login("cliente");
        await Exigir(await _http.GetAsync("/api/Chamados/agentes"), HttpStatusCode.Forbidden);

        await Login("admin");
        var agentes = await _http.GetFromJsonAsync<JsonElement>("/api/Chamados/agentes");
        Assert.Contains(agentes.EnumerateArray(), a => a.GetProperty("email").GetString() == "agente@test.invalid");
    }

    [Fact]
    public async Task SlaCategoriasRetornaOQueEstaCadastradoNoSeed()
    {
        await Login("cliente");
        var produtos = await _http.GetFromJsonAsync<JsonElement>("/api/SlaCategorias");
        var plataforma = Assert.Single(produtos.EnumerateArray(), p => p.GetProperty("produto").GetString() == DataSeeder.ProdutoPadrao);
        var categorias = plataforma.GetProperty("categorias").EnumerateArray().ToList();
        Assert.Equal(DataSeeder.CategoriasPadrao.Length, categorias.Count);
        Assert.All(categorias, c => Assert.Equal(4, c.GetProperty("prioridades").GetArrayLength()));
    }

    [Fact]
    public async Task LoginExigeSenhaCorretaENaoRevelaQuaisEmailsExistem()
    {
        var senhaErrada = await _http.PostAsJsonAsync("/api/Auth/login", new LoginDto("cliente@test.invalid", "senha-errada-123"));
        await Exigir(senhaErrada, HttpStatusCode.Unauthorized);
        var emailInexistente = await _http.PostAsJsonAsync("/api/Auth/login", new LoginDto("ninguem@test.invalid", SenhaTeste));
        await Exigir(emailInexistente, HttpStatusCode.Unauthorized);
        Assert.Equal(await senhaErrada.Content.ReadAsStringAsync(), await emailInexistente.Content.ReadAsStringAsync());
        Assert.Contains(AuthController.MensagemCredenciaisInvalidas, await senhaErrada.Content.ReadAsStringAsync());

        // Sem senha (SSO) e senha legada em texto puro: nunca entram pelo login por senha.
        await Exigir(await _http.PostAsJsonAsync("/api/Auth/login", new LoginDto("sso@test.invalid", "")), HttpStatusCode.Unauthorized);
        await Exigir(await _http.PostAsJsonAsync("/api/Auth/login", new LoginDto("sso@test.invalid", SenhaTeste)), HttpStatusCode.Unauthorized);
        await Exigir(await _http.PostAsJsonAsync("/api/Auth/login", new LoginDto("legado@test.invalid", "123456")), HttpStatusCode.Unauthorized);

        // E-mail com maiúsculas/espaços é normalizado; a resposta nunca traz o hash.
        var ok = await _http.PostAsJsonAsync("/api/Auth/login", new LoginDto("  Cliente@Test.Invalid ", SenhaTeste));
        await Exigir(ok, HttpStatusCode.OK);
        var corpo = await ok.Content.ReadAsStringAsync();
        Assert.DoesNotContain("senhaHash", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"token\"", corpo);
    }

    [Fact]
    public async Task LoginLimitaTentativasPorIp()
    {
        for (var i = 0; i < LimiteLoginPorMinuto; i++)
        {
            await Exigir(await _http.PostAsJsonAsync("/api/Auth/login", new LoginDto("cliente@test.invalid", "senha-errada-123")), HttpStatusCode.Unauthorized);
        }
        // Mesmo com a senha certa, o IP estourou a janela.
        var bloqueado = await _http.PostAsJsonAsync("/api/Auth/login", new LoginDto("cliente@test.invalid", SenhaTeste));
        await Exigir(bloqueado, HttpStatusCode.TooManyRequests);
        Assert.Contains("Muitas tentativas", await bloqueado.Content.ReadAsStringAsync());
        // Outras rotas não são afetadas pelo limite do login.
        await Exigir(await _http.GetAsync("/api/Auth/me"), HttpStatusCode.Unauthorized);
    }

    private async Task Login(string usuario)
    {
        var response = await _http.PostAsJsonAsync("/api/Auth/login", new LoginDto(usuario + "@test.invalid", SenhaTeste));
        await Exigir(response, HttpStatusCode.OK);
        var token = await response.Content.ReadFromJsonAsync<TokenResponseDto>();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.Token);
    }

    private async Task<int> CriarChamado()
    {
        await Login("cliente");
        var response = await _http.PostAsJsonAsync("/api/Chamados",
            new CriarChamadoDto("Plataforma", "Erro", "Teste de regressão", PrioridadeEnum.MEDIA));
        await Exigir(response, HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static async Task Exigir(HttpResponseMessage response, HttpStatusCode esperado) =>
        Assert.True(response.StatusCode == esperado,
            $"Esperado {(int)esperado}, recebido {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        await _connection.DisposeAsync();
        var fullPath = Path.GetFullPath(_uploads);
        var prefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "backend-api-tests-");
        if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullPath))
            Directory.Delete(fullPath, recursive: true);
    }

    private sealed class EventosEmMemoria : IEventoService
    {
        public List<string> Tipos { get; } = [];
        public Task PublicarAsync(string tipo, int? chamadoId, object payload, CancellationToken ct = default)
        {
            Tipos.Add(tipo);
            return Task.CompletedTask;
        }
    }
}
