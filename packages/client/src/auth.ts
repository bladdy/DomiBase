import { DomiBaseError } from "./errors.js";
import type { HttpTransport } from "./transport.js";

export interface AuthUser {
  id: string;
  email: string;
  createdAt: string;
}

export interface AuthSession {
  accessToken: string;
  refreshToken: string;
  expiresAt: string;
  user: AuthUser;
}

export interface SignUpCredentials {
  email: string;
  password: string;
}

export interface SignInCredentials {
  email: string;
  password: string;
}

export interface AuthResponse {
  accessToken: string;
  refreshToken: string;
  expiresAt: string;
  user: AuthUser | null;
}

export type AuthChangeEvent = "SIGNED_IN" | "SIGNED_OUT" | "TOKEN_REFRESHED";

export type AuthChangeHandler = (event: AuthChangeEvent, session: AuthSession | null) => void;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function parseUser(value: unknown): AuthUser | null {
  if (!isRecord(value)) {
    return null;
  }

  const id = typeof value.id === "string" ? value.id : "";
  const email = typeof value.email === "string" ? value.email : "";
  const createdAt = typeof value.createdAt === "string" ? value.createdAt : "";

  if (id.length === 0 || email.length === 0) {
    return null;
  }

  return { id, email, createdAt };
}

function parseSession(value: unknown): AuthSession | null {
  if (!isRecord(value)) {
    return null;
  }

  const accessToken = typeof value.accessToken === "string" ? value.accessToken : "";
  const refreshToken = typeof value.refreshToken === "string" ? value.refreshToken : "";
  const expiresAt = typeof value.expiresAt === "string" ? value.expiresAt : "";
  const user = parseUser(value.user);

  if (accessToken.length === 0 || refreshToken.length === 0 || expiresAt.length === 0 || user === null) {
    return null;
  }

  return { accessToken, refreshToken, expiresAt, user };
}

export interface AuthClient {
  getSession(): AuthSession | null;
  setSession(session: AuthSession | null): void;
  signUp(credentials: SignUpCredentials): Promise<{ data: AuthSession | null; error: DomiBaseError | null }>;
  signInWithPassword(credentials: SignInCredentials): Promise<{ data: AuthSession | null; error: DomiBaseError | null }>;
  signOut(): Promise<{ error: DomiBaseError | null }>;
  refreshSession(refreshToken: string): Promise<{ data: AuthSession | null; error: DomiBaseError | null }>;
  onAuthStateChange(handler: AuthChangeHandler): () => void;
}

interface WireAuthResponse {
  accessToken: string;
  refreshToken: string;
  expiresAt: string;
  user: AuthUser | null;
}

export class AuthClientImpl implements AuthClient {
  private session: AuthSession | null = null;
  private readonly listeners = new Set<AuthChangeHandler>();

  constructor(private readonly transport: HttpTransport) {}

  getSession(): AuthSession | null {
    return this.session;
  }

  setSession(session: AuthSession | null): void {
    this.emitSessionChange(session, session === null ? "SIGNED_OUT" : "SIGNED_IN");
  }

  async signUp(
    credentials: SignUpCredentials
  ): Promise<{ data: AuthSession | null; error: DomiBaseError | null }> {
    return this.sendSessionRequest("/auth/v1/register", credentials);
  }

  async signInWithPassword(
    credentials: SignInCredentials
  ): Promise<{ data: AuthSession | null; error: DomiBaseError | null }> {
    return this.sendSessionRequest("/auth/v1/login", credentials);
  }

  async signOut(): Promise<{ error: DomiBaseError | null }> {
    const refreshToken = this.session?.refreshToken;

    this.emitSessionChange(null, "SIGNED_OUT");

    if (refreshToken === undefined || refreshToken.length === 0) {
      return { error: null };
    }

    const result = await this.transport.send<unknown>({
      method: "POST",
      path: "/auth/v1/logout",
      query: new URLSearchParams(),
      body: { refreshToken }
    });

    return { error: result.error };
  }

  async refreshSession(
    refreshToken: string
  ): Promise<{ data: AuthSession | null; error: DomiBaseError | null }> {
    const result = await this.transport.send<WireAuthResponse>({
      method: "POST",
      path: "/auth/v1/refresh",
      query: new URLSearchParams(),
      body: { refreshToken }
    });

    if (result.error !== null || result.data === null) {
      return { data: null, error: result.error };
    }

    const session = parseSession(result.data);
    if (session === null) {
      return {
        data: null,
        error: new DomiBaseError({ code: "INVALID_RESPONSE", message: "The server returned an invalid session." })
      };
    }

    this.emitSessionChange(session, "TOKEN_REFRESHED");
    return { data: session, error: null };
  }

  onAuthStateChange(handler: AuthChangeHandler): () => void {
    this.listeners.add(handler);
    return () => {
      this.listeners.delete(handler);
    };
  }

  getAuthorizationHeader(): string | null {
    const token = this.session?.accessToken;
    return token !== undefined && token.length > 0 ? `Bearer ${token}` : null;
  }

  private async sendSessionRequest(
    path: string,
    body: SignUpCredentials | SignInCredentials
  ): Promise<{ data: AuthSession | null; error: DomiBaseError | null }> {
    const result = await this.transport.send<WireAuthResponse>({
      method: "POST",
      path,
      query: new URLSearchParams(),
      body
    });

    if (result.error !== null || result.data === null) {
      return { data: null, error: result.error };
    }

    const session = parseSession(result.data);
    if (session === null) {
      return {
        data: null,
        error: new DomiBaseError({ code: "INVALID_RESPONSE", message: "The server returned an invalid session." })
      };
    }

    this.emitSessionChange(session, "SIGNED_IN");
    return { data: session, error: null };
  }

  private emitSessionChange(session: AuthSession | null, event: AuthChangeEvent): void {
    this.session = session;
    for (const handler of this.listeners) {
      handler(event, session);
    }
  }
}
