import { useState } from 'react';
import { yupResolver } from '@hookform/resolvers/yup';
import { useForm } from 'react-hook-form';
import * as yup from 'yup';
import { useLocation, useNavigate } from 'react-router-dom';
import { useAuth } from '@/auth/AuthContext';
import { login as apiLogin } from '@/api/auth';
import { ApiError } from '@/api/client';

interface LoginValues {
  email: string;
  senha: string;
}

const schema: yup.ObjectSchema<LoginValues> = yup.object({
  email: yup.string().email('E-mail inválido').required('Campo obrigatório'),
  senha: yup.string().required('Campo obrigatório'),
});

interface LocationState {
  from?: { pathname: string };
}

/** 401 → credenciais inválidas; 429 → limite de tentativas; 0 → sem conexão; resto → mensagem genérica. */
function mensagemDeErro(err: unknown): string {
  if (err instanceof ApiError) {
    if (err.status === 401) return 'E-mail ou senha inválidos';
    if (err.status === 429) return 'Muitas tentativas. Aguarde um minuto e tente novamente.';
    if (err.status === 0) return err.message;
  }
  return 'Não foi possível entrar. Tente novamente.';
}

export function useApp() {
  const { login } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [erro, setErro] = useState<string | null>(null);
  const [enviando, setEnviando] = useState(false);

  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<LoginValues>({ resolver: yupResolver(schema) });

  const submit = handleSubmit(async (values) => {
    setErro(null);
    setEnviando(true);
    try {
      const { token } = await apiLogin(values.email, values.senha);
      await login(token);
      const destino = (location.state as LocationState | null)?.from?.pathname ?? '/chamados';
      navigate(destino, { replace: true });
    } catch (err) {
      setErro(mensagemDeErro(err));
    } finally {
      setEnviando(false);
    }
  });

  return { register, submit, errors, erro, enviando };
}
