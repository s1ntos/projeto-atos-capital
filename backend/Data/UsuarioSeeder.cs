using backend.Data.Repositories;
using backend.Enums;
using backend.Models;
using backend.Services;

namespace backend.Data;

/// <summary>Opções da seção "Seed" do appsettings (usuários iniciais).</summary>
public sealed class SeedOptions
{
    public const string Secao = "Seed";

    /// <summary>Cria os usuários de teste (admin@atos.com etc.). Só deve ficar true em desenvolvimento.</summary>
    public bool UsuariosDeTeste { get; set; }

    /// <summary>Senha dada aos usuários de teste que ainda não têm senha. Obrigatória quando UsuariosDeTeste = true.</summary>
    public string? SenhaUsuariosDeTeste { get; set; }

    /// <summary>Admin inicial para produção: criado só se o e-mail não existir; nunca sobrescreve senha já definida.</summary>
    public string? AdminEmail { get; set; }
    public string? AdminNome { get; set; }
    public string? AdminSenha { get; set; }
}

public static class UsuarioSeeder
{
    public static readonly (string Nome, string Email, string IdExterno, PapelEnum Papel, bool GrupoNortec)[] UsuariosDeTeste =
    [
        ("Admin Atos", "admin@atos.com", "USR-ADM-001", PapelEnum.ADMIN, false),
        ("Cliente Teste", "cliente@atos.com", "USR-CLI-001", PapelEnum.CLIENTE, false),
        ("Agente Atos", "agente@atos.com", "USR-AGT-001", PapelEnum.AGENTE, false),
        ("Supervisor Atos", "supervisor@atos.com", "USR-SUP-001", PapelEnum.SUPERVISOR, false),
        ("Cliente Nortec", "cliente@nortec.com", "USR-CLI-002", PapelEnum.CLIENTE, true),
    ];

    /// <summary>
    /// Zera qualquer SenhaHash que não esteja no formato de hash (senhas gravadas em texto puro por seeds antigos).
    /// Esses usuários passam a entrar só por SSO até receberem uma senha nova. Devolve quantos foram alterados.
    /// </summary>
    public static int ZerarSenhasEmTextoPuro(IBaseRepository<Usuario> usuarios, ILogger logger)
    {
        var alterados = 0;
        foreach (var usuario in usuarios.ObterTodos().Where(u => u.SenhaHash != "").ToList())
        {
            if (SenhaService.PareceHash(usuario.SenhaHash)) continue;
            usuario.SenhaHash = string.Empty;
            usuarios.Edit(usuario);
            alterados++;
            logger.LogWarning("Senha em texto puro removida do usuário {UsuarioId} ({Email}); defina uma senha nova.", usuario.Id, usuario.Email);
        }
        if (alterados > 0) usuarios.SalvarAlteracoes();
        return alterados;
    }

    /// <summary>Garante os usuários de teste (idempotente) e dá a senha configurada aos que ainda não têm.</summary>
    public static void SeedUsuariosDeTeste(
        IBaseRepository<Usuario> usuarios, SenhaService senhas, SeedOptions opcoes, int grupoAtosId, int grupoNortecId, ILogger logger)
    {
        var erroSenha = SenhaService.ValidarPolitica(opcoes.SenhaUsuariosDeTeste);
        if (erroSenha != null)
        {
            throw new InvalidOperationException($"Seed:SenhaUsuariosDeTeste inválida: {erroSenha}");
        }

        foreach (var (nome, email, idExterno, papel, nortec) in UsuariosDeTeste)
        {
            var usuario = usuarios.ObterTodos().FirstOrDefault(u => u.Email == email);
            if (usuario == null)
            {
                usuario = new Usuario
                {
                    Nome = nome,
                    Email = email,
                    IdExterno = idExterno,
                    Papel = papel,
                    GrupoEmpresaId = nortec ? grupoNortecId : grupoAtosId,
                };
                usuario.SenhaHash = senhas.GerarHash(usuario, opcoes.SenhaUsuariosDeTeste!);
                usuarios.Add(usuario);
            }
            else if (string.IsNullOrEmpty(usuario.SenhaHash))
            {
                usuario.SenhaHash = senhas.GerarHash(usuario, opcoes.SenhaUsuariosDeTeste!);
                usuarios.Edit(usuario);
            }
        }
        usuarios.SalvarAlteracoes();
        logger.LogInformation("Usuários de teste garantidos com a senha de Seed:SenhaUsuariosDeTeste.");
    }

    /// <summary>Admin inicial (produção): cria se não existir ou define a senha se o usuário existe sem senha.</summary>
    public static void SeedAdminInicial(IBaseRepository<Usuario> usuarios, SenhaService senhas, SeedOptions opcoes, int grupoAtosId, ILogger logger)
    {
        var email = (opcoes.AdminEmail ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email) && string.IsNullOrEmpty(opcoes.AdminSenha)) return;

        var erroSenha = SenhaService.ValidarPolitica(opcoes.AdminSenha);
        if (string.IsNullOrEmpty(email) || erroSenha != null)
        {
            throw new InvalidOperationException($"Seed:AdminEmail/AdminSenha inválidos: {erroSenha ?? "e-mail vazio"}");
        }

        var usuario = usuarios.ObterTodos().FirstOrDefault(u => u.Email.ToLower() == email);
        if (usuario == null)
        {
            usuario = new Usuario
            {
                Nome = string.IsNullOrWhiteSpace(opcoes.AdminNome) ? "Administrador" : opcoes.AdminNome.Trim(),
                Email = email,
                IdExterno = "USR-ADM-SEED",
                Papel = PapelEnum.ADMIN,
                GrupoEmpresaId = grupoAtosId,
            };
            usuario.SenhaHash = senhas.GerarHash(usuario, opcoes.AdminSenha!);
            usuarios.Add(usuario);
            usuarios.SalvarAlteracoes();
            logger.LogInformation("Admin inicial {Email} criado a partir de Seed:AdminEmail.", email);
        }
        else if (string.IsNullOrEmpty(usuario.SenhaHash))
        {
            usuario.SenhaHash = senhas.GerarHash(usuario, opcoes.AdminSenha!);
            usuarios.Edit(usuario);
            usuarios.SalvarAlteracoes();
            logger.LogInformation("Senha do admin inicial {Email} definida a partir de Seed:AdminSenha.", email);
        }
        // Já tem senha: não sobrescreve.
    }
}
