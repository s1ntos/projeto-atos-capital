import { request, setToken } from './client';
import type { TokenResponse, UsuarioLogado } from './types';

/**
 * Login por e-mail e senha. A API responde 401 com "E-mail ou senha inválidos." para qualquer
 * falha de credencial e 429 quando o IP excede o limite de tentativas; os dois chegam aqui
 * como ApiError (sem redirecionar, para o formulário mostrar a mensagem).
 */
export async function login(email: string, senha: string): Promise<TokenResponse> {
  const resposta = await request<TokenResponse>('/api/Auth/login', {
    method: 'POST',
    body: { email, senha },
    semRedirecionamento401: true,
  });
  setToken(resposta.token);
  return resposta;
}

export function me(): Promise<UsuarioLogado> {
  return request<UsuarioLogado>('/api/Auth/me');
}

/**
 * SSO: troca o token assinado pelo portal Atos Capital (recebido na URL de acesso à
 * tela de suporte) por um token da própria API, fazendo upsert transparente do usuário.
 */
export function sso(tokenPortal: string): Promise<TokenResponse> {
  return request<TokenResponse>('/api/Auth/sso', {
    method: 'POST',
    body: { token: tokenPortal },
    semRedirecionamento401: true,
  });
}

/**
 * Endpoint ainda não existe no backend (não há fluxo de recuperação de senha
 * implementado). Fica pronto no front para quando o backend expuser a rota.
 */
export function esqueciSenha(email: string): Promise<void> {
  return request<void>('/api/Auth/esqueci-senha', {
    method: 'POST',
    body: { email },
  });
}
