const SESSION_TOKEN_KEY = "domibase.admin.token";

/** Session-only storage: the token disappears when the browser tab closes. */
export function readSessionToken(): string | null {
  try {
    return globalThis.sessionStorage?.getItem(SESSION_TOKEN_KEY) ?? null;
  } catch {
    return null;
  }
}

export function writeSessionToken(token: string): void {
  try {
    globalThis.sessionStorage?.setItem(SESSION_TOKEN_KEY, token);
  } catch {
    // A blocked storage must not break the in-memory session.
  }
}

export function clearSessionToken(): void {
  try {
    globalThis.sessionStorage?.removeItem(SESSION_TOKEN_KEY);
  } catch {
    // Nothing else to do: the in-memory token is cleared by the caller.
  }
}
