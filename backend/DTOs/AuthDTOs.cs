using backend.Enums;

namespace backend.DTOs;

public record LoginDto(
    string Email,
    string Senha
);

/// <summary>Token JWT assinado pelo portal Atos Capital, recebido via SSO (ver POST /api/Auth/sso).</summary>
public record SsoLoginDto(
    string Token
);

public record TokenResponseDto(
    string Token,
    string Nome,
    string Email,
    PapelEnum Papel,
    int GrupoEmpresaId
);

/// <summary>Retorno de GET /api/Auth/me. Nunca inclui SenhaHash.</summary>
public record UsuarioMeDto(
    int Id,
    string Nome,
    string Email,
    PapelEnum Papel,
    int GrupoEmpresaId,
    string GrupoEmpresaNome
);
