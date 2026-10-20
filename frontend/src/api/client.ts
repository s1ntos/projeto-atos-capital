import { notify } from '@/utils/notify';

export const BASE_URL = import.meta.env.VITE_API_URL;

/** Chave usada para o token de sessão em sessionStorage (lido/gravado também pelo login e pelo AuthContext). */
export const TOKEN_STORAGE_KEY = 'atos:token';

export function getToken(): string | null {
  return sessionStorage.getItem(TOKEN_STORAGE_KEY);
}

export function setToken(token: string): void {
  sessionStorage.setItem(TOKEN_STORAGE_KEY, token);
}

export function clearToken(): void {
  sessionStorage.removeItem(TOKEN_STORAGE_KEY);
}

/** Monta a query string ignorando valores nulos/indefinidos/vazios. */
export function toQueryString(params: Record<string, string | number | boolean | null | undefined>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== null && value !== undefined && value !== '') search.set(key, String(value));
  }
  const query = search.toString();
  return query ? `?${query}` : '';
}

export class ApiError extends Error {
  status: number;

  constructor(message: string, status: number) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

interface RequestOptions extends Omit<RequestInit, 'body'> {
  body?: BodyInit | object | number | string | boolean | null;
  /**
   * Não limpar a sessão nem redirecionar para /login quando a API responder 401.
   * Usado pelas chamadas de autenticação (login/SSO), onde 401 significa "credenciais inválidas"
   * e precisa chegar ao formulário como erro, não como sessão expirada.
   */
  semRedirecionamento401?: boolean;
}

function isFormData(body: RequestOptions['body']): body is FormData {
  return body instanceof FormData;
}

async function parseBody(response: Response): Promise<unknown> {
  if (response.status === 204) return undefined;
  const contentType = response.headers.get('content-type') ?? '';
  if (!contentType.includes('application/json')) return undefined;
  try {
    return await response.json();
  } catch {
    return undefined;
  }
}

function extractMessage(data: unknown, fallback: string): string {
  // Vários endpoints (ex.: AuthController) devolvem só a string do erro (Unauthorized("..."),
  // BadRequest("...")), não um objeto { message }.
  if (typeof data === 'string' && data.length > 0) {
    return data;
  }
  if (data && typeof data === 'object' && 'message' in data && typeof (data as { message: unknown }).message === 'string') {
    return (data as { message: string }).message;
  }
  return fallback;
}

/**
 * Camada única de chamadas HTTP da aplicação.
 * 401 → limpa a sessão e redireciona para /login (exceto com semRedirecionamento401). 403/409 → notifica via toast com a mensagem da API.
 */
export async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { body, headers, semRedirecionamento401, ...rest } = options;
  const finalHeaders = new Headers(headers);

  const token = getToken();
  if (token) {
    finalHeaders.set('Authorization', `Bearer ${token}`);
  }

  const formData = isFormData(body);
  if (!formData && body != null && !finalHeaders.has('Content-Type')) {
    finalHeaders.set('Content-Type', 'application/json');
  }

  let response: Response;
  try {
    response = await fetch(`${BASE_URL}${path}`, {
      ...rest,
      headers: finalHeaders,
      body: formData ? body : body != null ? JSON.stringify(body) : undefined,
    });
  } catch {
    throw new ApiError('Não foi possível conectar ao servidor.', 0);
  }

  if (response.status === 401 && !semRedirecionamento401) {
    clearToken();
    window.location.assign('/login');
    throw new ApiError('Sessão expirada.', 401);
  }

  const data = await parseBody(response);

  if (!response.ok) {
    const message = extractMessage(data, 'Ocorreu um erro ao processar a solicitação.');
    if (response.status === 403 || response.status === 409) {
      notify.error(message);
    }
    throw new ApiError(message, response.status);
  }

  return data as T;
}
