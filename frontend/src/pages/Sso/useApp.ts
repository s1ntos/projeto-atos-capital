import { useEffect, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { useAuth } from '@/auth/AuthContext';
import { sso as apiSso } from '@/api/auth';
import { ApiError } from '@/api/client';

interface LocationState {
  from?: { pathname: string };
}

/**
 * Entrada via portal Atos Capital: o portal redireciona o usuário para aqui com
 * `?token=<jwt assinado pelo portal>`. A entrada padrão é a tela de login (/login);
 * esta rota continua para quem vem autenticado do portal.
 */
export function useApp() {
  const { login } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [erro, setErro] = useState<string | null>(null);
  const [tokenPortal] = useState(() => new URLSearchParams(location.search).get('token'));

  useEffect(() => {
    if (!tokenPortal) return;
    let ativo = true;

    (async () => {
      try {
        const { token } = await apiSso(tokenPortal);
        await login(token);
        if (!ativo) return;
        const destino = (location.state as LocationState | null)?.from?.pathname ?? '/chamados';
        navigate(destino, { replace: true });
      } catch (err) {
        if (!ativo) return;
        setErro(err instanceof ApiError ? err.message : 'Não foi possível validar o acesso vindo do portal.');
      }
    })();

    return () => {
      ativo = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- roda uma única vez para o token recebido na URL
  }, [tokenPortal]);

  return { erro, semToken: !tokenPortal };
}
