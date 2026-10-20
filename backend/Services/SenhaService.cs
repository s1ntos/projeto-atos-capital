using backend.Models;
using Microsoft.AspNetCore.Identity;

namespace backend.Services;

public enum ResultadoVerificacaoSenha
{
    Falha,
    Sucesso,
    /// <summary>Senha correta, mas o hash está em formato antigo e deve ser regravado.</summary>
    SucessoPrecisaRehash,
}

/// <summary>
/// Hash e verificação de senha (PBKDF2 via <see cref="PasswordHasher{TUser}"/> do ASP.NET Core).
/// Nunca guarda nem registra a senha em texto puro.
/// </summary>
public sealed class SenhaService
{
    public const int TamanhoMinimo = 8;
    public const int TamanhoMaximo = 128;

    private static readonly Usuario UsuarioVazio = new();

    private readonly IPasswordHasher<Usuario> _hasher;

    // Hash de uma senha aleatória, usado quando o e-mail não existe ou o usuário não tem senha:
    // a verificação roda do mesmo jeito e o tempo de resposta não revela quais e-mails estão cadastrados.
    private readonly string _hashFicticio;

    public SenhaService(IPasswordHasher<Usuario> hasher)
    {
        _hasher = hasher;
        _hashFicticio = _hasher.HashPassword(UsuarioVazio, Guid.NewGuid().ToString("N"));
    }

    public string GerarHash(Usuario usuario, string senha) => _hasher.HashPassword(usuario, senha);

    /// <summary>
    /// Verifica a senha informada. Aceita usuário nulo (e-mail inexistente) e usuário sem senha
    /// (criado via SSO): nos dois casos devolve Falha, mas só depois de rodar a verificação fictícia.
    /// </summary>
    public ResultadoVerificacaoSenha Verificar(Usuario? usuario, string senha)
    {
        var hash = usuario?.SenhaHash;
        var temHashValido = !string.IsNullOrEmpty(hash) && PareceHash(hash);

        var resultado = _hasher.VerifyHashedPassword(
            usuario ?? UsuarioVazio,
            temHashValido ? hash! : _hashFicticio,
            senha);

        if (!temHashValido) return ResultadoVerificacaoSenha.Falha;

        return resultado switch
        {
            PasswordVerificationResult.Success => ResultadoVerificacaoSenha.Sucesso,
            PasswordVerificationResult.SuccessRehashNeeded => ResultadoVerificacaoSenha.SucessoPrecisaRehash,
            _ => ResultadoVerificacaoSenha.Falha,
        };
    }

    /// <summary>Devolve a mensagem de erro da política de senha, ou null se a senha é aceitável.</summary>
    public static string? ValidarPolitica(string? senha)
    {
        if (string.IsNullOrWhiteSpace(senha)) return "Informe uma senha.";
        if (senha.Length < TamanhoMinimo) return $"A senha deve ter pelo menos {TamanhoMinimo} caracteres.";
        if (senha.Length > TamanhoMaximo) return $"A senha deve ter no máximo {TamanhoMaximo} caracteres.";
        return null;
    }

    /// <summary>
    /// Indica se o valor tem o formato produzido pelo PasswordHasher (base64 com byte de versão 0x00 ou 0x01).
    /// Serve para detectar senhas gravadas em texto puro por versões antigas do seed.
    /// </summary>
    public static bool PareceHash(string valor)
    {
        if (string.IsNullOrEmpty(valor) || valor.Length < 40) return false;
        Span<byte> buffer = stackalloc byte[valor.Length];
        if (!Convert.TryFromBase64String(valor, buffer, out var escritos) || escritos < 32) return false;
        return buffer[0] is 0x00 or 0x01;
    }
}
