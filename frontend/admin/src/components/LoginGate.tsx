import { useState, type FormEvent } from "react";

interface LoginGateProps {
  readonly error: string | null;
  readonly busy: boolean;
  readonly onLogin: (username: string, password: string) => void;
}

export function LoginGate({ error, busy, onLogin }: LoginGateProps) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [revealed, setRevealed] = useState(false);
  const [localError, setLocalError] = useState<string | null>(null);

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const user = username.trim();
    if (user.length === 0 || password.length === 0) {
      setLocalError("Introduce el usuario y la contraseña del propietario.");
      return;
    }
    setLocalError(null);
    onLogin(user, password);
  }

  const message = localError ?? error;

  return (
    <main className="gate">
      <div className="gate__sheet">
        <div className="gate__mark" aria-hidden="true" />
        <p className="gate__brand">DomiBase</p>
        <h1 className="gate__title">Consola de administración</h1>
        <p className="gate__lead">
          Acceso privado del propietario. Gestiona proyectos, tablas y registros desde aquí.
        </p>

        <form className="form form--login" onSubmit={handleSubmit} noValidate>
          <div className="field">
            <label htmlFor="admin-user">Usuario</label>
            <input
              id="admin-user"
              name="username"
              type="text"
              value={username}
              onChange={(event) => {
                setUsername(event.target.value);
                setLocalError(null);
              }}
              autoComplete="username"
              spellCheck={false}
              aria-invalid={message ? "true" : undefined}
              aria-describedby={message ? "login-message" : undefined}
            />
          </div>

          <div className="field">
            <label htmlFor="admin-password">Contraseña</label>
            <div className="field__row">
              <input
                id="admin-password"
                name="password"
                type={revealed ? "text" : "password"}
                value={password}
                onChange={(event) => {
                  setPassword(event.target.value);
                  setLocalError(null);
                }}
                autoComplete="current-password"
                aria-invalid={message ? "true" : undefined}
                aria-describedby={message ? "login-message" : "session-hint"}
              />
              <button
                type="button"
                className="button button--ghost button--small"
                onClick={() => setRevealed((current) => !current)}
                aria-pressed={revealed}
              >
                {revealed ? "Ocultar" : "Mostrar"}
              </button>
            </div>
            <p id="session-hint" className="hint">
              La sesión firmada se guarda solo en esta pestaña y caduca automáticamente.
            </p>
          </div>

          {message ? (
            <p id="login-message" className="form__error" role="alert">
              {message}
            </p>
          ) : null}

          <button type="submit" className="button button--primary" disabled={busy}>
            {busy ? "Entrando…" : "Entrar"}
          </button>
        </form>
      </div>
    </main>
  );
}
