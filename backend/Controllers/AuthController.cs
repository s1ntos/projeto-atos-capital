using System.Security.Claims;
using backend.Data.Repositories;
using backend.DTOs;
using backend.Models;
using backend.Seguranca;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    /// <summary>Mensagem única para e-mail inexistente, senha errada e usuário sem senha (não revela quais e-mails existem).</summary>
    public const string MensagemCredenciaisInvalidas = "E-mail ou senha inválidos.";

    private readonly IBaseRepository<Usuario> _usuarios;
    private readonly IBaseRepository<GrupoEmpresa> _grupos;
    private readonly AuthService _authService;
    private readonly SenhaService _senhas;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IBaseRepository<Usuario> usuarios,
        IBaseRepository<GrupoEmpresa> grupos,
        AuthService authService,
        SenhaService senhas,
        ILogger<AuthController> logger)
    {
        _usuarios = usuarios;
        _grupos = grupos;
        _authService = authService;
        _senhas = senhas;
        _logger = logger;
    }

    /// <summary>
    /// Login por e-mail e senha. Responde 401 com a mesma mensagem para qualquer falha e 429 quando o IP
    /// excede o limite de tentativas (ver LoginRateLimit).
    /// </summary>
    [HttpPost("login")]
    [EnableRateLimiting(LoginRateLimit.Politica)]
    public async Task<ActionResult<TokenResponseDto>> Login([FromBody] LoginDto dto)
    {
        var email = NormalizarEmail(dto.Email);
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "desconhecido";

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(dto.Senha) || dto.Senha.Length > SenhaService.TamanhoMaximo)
        {
            _logger.LogWarning("Login recusado (credenciais ausentes ou fora do tamanho) a partir de {Ip}", ip);
            return Unauthorized(MensagemCredenciaisInvalidas);
        }

        var usuario = await _usuarios.ObterTodos()
            .FirstOrDefaultAsync(u => u.Email.ToLower() == email);

        // Verificar sempre roda o hash (fictício quando não há usuário/senha) para manter o tempo de resposta uniforme.
        var resultado = _senhas.Verificar(usuario, dto.Senha);
        if (resultado == ResultadoVerificacaoSenha.Falha)
        {
            _logger.LogWarning("Login falhou para {Email} a partir de {Ip}", email, ip);
            return Unauthorized(MensagemCredenciaisInvalidas);
        }

        if (resultado == ResultadoVerificacaoSenha.SucessoPrecisaRehash)
        {
            usuario!.SenhaHash = _senhas.GerarHash(usuario, dto.Senha);
            usuario = await _usuarios.EditarESalvarAsync(usuario);
        }

        _logger.LogInformation("Login bem-sucedido do usuário {UsuarioId} a partir de {Ip}", usuario!.Id, ip);
        var token = _authService.GerarToken(usuario);

        return Ok(new TokenResponseDto(
            token,
            usuario.Nome,
            usuario.Email,
            usuario.Papel,
            usuario.GrupoEmpresaId
        ));
    }

    /// <summary>
    /// SSO: troca um token assinado pelo portal Atos Capital por um token da API de suporte,
    /// fazendo upsert transparente do usuário e do grupo empresa (sem sincronização em lote prévia).
    /// </summary>
    [HttpPost("sso")]
    public async Task<ActionResult<TokenResponseDto>> Sso([FromBody] SsoLoginDto dto)
    {
        var claims = _authService.ValidarTokenSso(dto.Token);
        if (claims == null)
        {
            return Unauthorized("Token SSO inválido ou expirado.");
        }

        Usuario usuario;
        try
        {
            usuario = await _authService.UpsertUsuarioSsoAsync(claims, _grupos, _usuarios);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }

        var token = _authService.GerarToken(usuario);

        return Ok(new TokenResponseDto(
            token,
            usuario.Nome,
            usuario.Email,
            usuario.Papel,
            usuario.GrupoEmpresaId
        ));
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<UsuarioMeDto>> Me()
    {
        var idClaim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(idClaim) || !int.TryParse(idClaim, out var id))
        {
            return Unauthorized();
        }

        var usuario = await _usuarios.ObterTodos()
            .Include(u => u.GrupoEmpresa)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (usuario == null)
        {
            return Unauthorized();
        }

        return Ok(new UsuarioMeDto(
            usuario.Id,
            usuario.Nome,
            usuario.Email,
            usuario.Papel,
            usuario.GrupoEmpresaId,
            usuario.GrupoEmpresa.Nome
        ));
    }

    public static string NormalizarEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();
}
