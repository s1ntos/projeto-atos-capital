import { Alert, Box, Button, CircularProgress, Typography } from '@mui/material';
import { Link as RouterLink } from 'react-router-dom';
import { useTheme } from '@mui/material/styles';
import { useMediaQuery } from '@mui/material';
import { brand, logos } from '@/theme/tokens';
import type { ThemeMode } from '@/theme/tokens';
import { useApp } from './useApp';

/**
 * Tela de entrada via SSO: o portal Atos Capital redireciona para aqui com o token assinado.
 * Sem token, oferece o caminho para a tela de login por e-mail e senha.
 */
export function Sso() {
  const { erro, semToken } = useApp();
  const theme = useTheme();
  const mode = theme.palette.mode as ThemeMode;
  const b = brand[mode];
  const isMdUp = useMediaQuery(theme.breakpoints.up('md'));

  return (
    <Box sx={{ minHeight: '100vh', display: 'flex', bgcolor: 'background.default' }}>
      {isMdUp && (
        <Box
          sx={{
            flexBasis: '46%',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            p: 6,
            color: '#fff',
            background: `linear-gradient(160deg, ${b.primaryStrong}, ${b.primaryMuted})`,
          }}
        >
          <Box component="img" src={logos.logo} alt="Atos Capital" sx={{ height: 26, filter: 'brightness(0) invert(1)' }} />
        </Box>
      )}

      <Box sx={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', p: 3 }}>
        <Box sx={{ width: '100%', maxWidth: 360, textAlign: 'center' }}>
          {!isMdUp && (
            <Box component="img" src={logos.logo} alt="Atos Capital" sx={{ height: 26, mb: 4, display: 'block', mx: 'auto' }} />
          )}

          {semToken && !erro && (
            <>
              <Typography variant="h1" sx={{ fontSize: 20, mb: 1 }}>
                Acesso pelo portal Atos Capital
              </Typography>
              <Typography variant="body2" color="text.secondary" sx={{ mb: 3 }}>
                Acesse pelo portal Atos Capital para entrar automaticamente, ou use seu e-mail e senha.
              </Typography>
              <Button component={RouterLink} to="/login" variant="contained" fullWidth sx={{ minHeight: 44 }}>
                Entrar com e-mail e senha
              </Button>
            </>
          )}

          {!semToken && !erro && (
            <>
              <CircularProgress size={28} sx={{ mb: 2 }} />
              <Typography variant="body2" color="text.secondary">
                Validando seu acesso...
              </Typography>
            </>
          )}

          {erro && (
            <Alert severity="error" sx={{ textAlign: 'left' }}>
              {erro} Volte ao portal Atos Capital e tente novamente.
            </Alert>
          )}
        </Box>
      </Box>
    </Box>
  );
}
