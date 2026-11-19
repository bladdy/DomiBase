import {
  createClient,
  type DomiBaseClient,
  type DomiBaseError,
  type DomiBaseResponse,
  type Row
} from "@domibase/client";

/**
 * Demo-only shapes kept local to the example. Auth/Storage/Functions/Realtime are not part of
 * the supported SDK surface and are therefore not advertised here.
 */
export interface DemoAuthUser {
  readonly id: string;
  readonly email: string;
  readonly createdAt: string;
}

export interface AuthSession {
  readonly accessToken: string;
  readonly refreshToken: string;
  readonly expiresAt: string;
  readonly user: DemoAuthUser;
}

export interface StorageObject {
  readonly id: string;
  readonly path: string;
  readonly contentType: string | null;
  readonly size: number;
  readonly etag: string;
  readonly createdAt: string;
}

export interface SignedUrl {
  readonly url: string;
}
import {
  DOMIBASE_API_URL,
  DOMIBASE_PROJECT_KEY,
  DOMIBASE_PROJECT_KEY_B,
  DOMIBASE_PROJECT_NAME,
  DOMIBASE_PROJECT_NAME_B,
  DOMIBASE_REVOKED_PROJECT_KEY
} from "astro:env/server";

/** Demo table. All identifiers are still validated server-side by the Runtime. */
export const CONTACTS_TABLE = "contactos";

export const CONTACT_COLUMNS = "id,nombre,email,estado,notas,created_at";

export const CONTACT_ESTADOS = ["nuevo", "contactado", "cliente", "archivado"] as const;

export type ContactoEstado = (typeof CONTACT_ESTADOS)[number];

export interface Contacto extends Row {
  id: string;
  nombre: string;
  email: string | null;
  estado: string;
  notas: string | null;
  created_at: string;
}

export interface Nota extends Row {
  id: string;
  owner_id: string;
  titulo: string;
  contenido: string | null;
  created_at: string;
}

export type ProjectKind = "primary" | "secondary";

export const apiUrl = DOMIBASE_API_URL;

export const primaryProject = {
  key: "primary" as const,
  name: DOMIBASE_PROJECT_NAME
};

export const secondaryProject = DOMIBASE_PROJECT_KEY_B
  ? {
      key: "secondary" as const,
      name: DOMIBASE_PROJECT_NAME_B
    }
  : null;

export const revokedProjectKey = DOMIBASE_REVOKED_PROJECT_KEY;

const clients = new Map<ProjectKind, DomiBaseClient>();

/**
 * Server-only client factory. Keys come from astro:env/server and are never rendered,
 * serialized into page props or included in a client bundle.
 */
export function projectClient(kind: ProjectKind): DomiBaseClient | null {
  const projectKey = kind === "primary" ? DOMIBASE_PROJECT_KEY : DOMIBASE_PROJECT_KEY_B;

  if (!projectKey) {
    return null;
  }

  const existing = clients.get(kind);
  if (existing) {
    return existing;
  }

  const client = createClient({ url: DOMIBASE_API_URL, projectKey });
  clients.set(kind, client);
  return client;
}

export async function listContacts(
  kind: ProjectKind = "primary"
): Promise<DomiBaseResponse<Contacto[]>> {
  const client = projectClient(kind);
  if (!client) {
    return { data: null, error: null, count: null };
  }

  return client
    .from<Contacto>(CONTACTS_TABLE)
    .select(CONTACT_COLUMNS)
    .order("created_at", { ascending: false })
    .limit(50);
}

export interface NewContactInput {
  nombre: string;
  email: string | null;
  estado: ContactoEstado;
  notas: string | null;
}

export async function createContact(
  input: NewContactInput
): Promise<DomiBaseResponse<Contacto>> {
  const client = requireProjectClient("primary");

  const row: Partial<Contacto> = {
    nombre: input.nombre,
    estado: input.estado
  };

  if (input.email !== null) {
    row.email = input.email;
  }

  if (input.notas !== null) {
    row.notas = input.notas;
  }

  return client
    .from<Contacto>(CONTACTS_TABLE)
    .insert(row)
    .select(CONTACT_COLUMNS)
    .single();
}

export async function updateContactEstado(
  id: string,
  estado: ContactoEstado
): Promise<DomiBaseResponse<Contacto[]>> {
  return requireProjectClient("primary")
    .from<Contacto>(CONTACTS_TABLE)
    .update({ estado })
    .eq("id", id)
    .select("id,estado")
    .limit(1);
}

export async function deleteContact(id: string): Promise<DomiBaseResponse<Contacto[]>> {
  return requireProjectClient("primary")
    .from<Contacto>(CONTACTS_TABLE)
    .delete()
    .eq("id", id)
    .select("id")
    .limit(1);
}

/** Short, secret-free description suitable for the page. */
export function describeError(error: DomiBaseError | null): string {
  if (!error) {
    return "Error desconocido.";
  }

  return `${error.code}: ${error.message}`;
}

export function statusRoute(error: DomiBaseError | null): string {
  if (!error) {
    return "unexpected";
  }

  if (error.code === "FORBIDDEN" || error.code === "PROJECT_NOT_READY") {
    return "not_allowed";
  }

  if (
    error.code === "DATABASE_UNAVAILABLE" ||
    error.code === "PROJECT_UNAVAILABLE" ||
    error.code === "DATABASE_ERROR" ||
    error.code === "INTERNAL_ERROR"
  ) {
    return "unavailable";
  }

  if (error.code === "INVALID_API_KEY" || error.code === "MISSING_API_KEY") {
    return "configuration";
  }

  return "unexpected";
}

function requireProjectClient(kind: ProjectKind): DomiBaseClient {
  const client = projectClient(kind);
  if (!client) {
    throw new Error(`Project '${kind}' is not configured. Run scripts/dev/phase-06-demo.mjs first.`);
  }

  return client;
}

/** Server-only authenticated client. The session must come from a secure cookie. */
export function authenticatedClient(session: AuthSession | null): DomiBaseClient | null {
  const projectKey = DOMIBASE_PROJECT_KEY;
  if (!projectKey || !session) {
    return null;
  }

  const client = createClient({ url: DOMIBASE_API_URL, projectKey });
  client.auth.setSession(session);
  return client;
}

/** Cookie names used to persist the auth session on the server. */
export const AUTH_COOKIES = {
  accessToken: "domibase_access_token",
  refreshToken: "domibase_refresh_token",
  expiresAt: "domibase_expires_at",
  userId: "domibase_user_id",
  userEmail: "domibase_user_email"
} as const;

export function serializeSession(session: AuthSession): Record<string, string> {
  return {
    [AUTH_COOKIES.accessToken]: session.accessToken,
    [AUTH_COOKIES.refreshToken]: session.refreshToken,
    [AUTH_COOKIES.expiresAt]: session.expiresAt,
    [AUTH_COOKIES.userId]: session.user.id,
    [AUTH_COOKIES.userEmail]: session.user.email
  };
}

export function readSessionFromCookies(cookies: {
  get(name: string): { value: string } | undefined;
}): AuthSession | null {
  const accessToken = cookies.get(AUTH_COOKIES.accessToken)?.value;
  const refreshToken = cookies.get(AUTH_COOKIES.refreshToken)?.value;
  const expiresAt = cookies.get(AUTH_COOKIES.expiresAt)?.value;
  const userId = cookies.get(AUTH_COOKIES.userId)?.value;
  const userEmail = cookies.get(AUTH_COOKIES.userEmail)?.value;

  if (!accessToken || !refreshToken || !expiresAt || !userId || !userEmail) {
    return null;
  }

  return {
    accessToken,
    refreshToken,
    expiresAt,
    user: { id: userId, email: userEmail, createdAt: "" }
  };
}

export async function signInWithPassword(
  email: string,
  password: string
): Promise<{ session: AuthSession | null; error: DomiBaseError | null }> {
  const client = requireProjectClient("primary");
  const result = await client.auth.signInWithPassword({ email, password });
  return { session: result.data, error: result.error };
}

export async function signUp(
  email: string,
  password: string
): Promise<{ session: AuthSession | null; error: DomiBaseError | null }> {
  const client = requireProjectClient("primary");
  const result = await client.auth.signUp({ email, password });
  return { session: result.data, error: result.error };
}

export async function listNotes(session: AuthSession | null): Promise<DomiBaseResponse<Nota[]>> {
  const client = authenticatedClient(session);
  if (!client) {
    return { data: null, error: null, count: null };
  }

  return client
    .from<Nota>("notas")
    .select("id,titulo,contenido,created_at")
    .order("created_at", { ascending: false })
    .limit(50);
}

export async function createNote(
  session: AuthSession | null,
  input: { titulo: string; contenido: string | null }
): Promise<DomiBaseResponse<Nota>> {
  const client = authenticatedClient(session);
  if (!client) {
    return { data: null, error: null, count: null };
  }

  const row: Partial<Nota> = { titulo: input.titulo };
  if (input.contenido !== null) {
    row.contenido = input.contenido;
  }

  return client.from<Nota>("notas").insert(row).select("id,titulo,contenido,created_at").single();
}

/** Demo storage bucket. The bucket itself is created by the provisioning script. */
export const DEMO_BUCKET = "demo-publico";

export async function listStoredObjects(
  session: AuthSession | null
): Promise<DomiBaseResponse<StorageObject[]>> {
  const client = authenticatedClient(session);
  if (!client) {
    return { data: null, error: null, count: null };
  }

  return client.storage.from(DEMO_BUCKET).list();
}

export async function uploadTextObject(
  session: AuthSession | null,
  path: string,
  content: string
): Promise<DomiBaseResponse<StorageObject>> {
  const client = authenticatedClient(session);
  if (!client) {
    return { data: null, error: null, count: null };
  }

  return client.storage.from(DEMO_BUCKET).upload(path, content, { contentType: "text/plain" });
}

export async function removeStoredObject(
  session: AuthSession | null,
  path: string
): Promise<DomiBaseResponse<boolean>> {
  const client = authenticatedClient(session);
  if (!client) {
    return { data: null, error: null, count: null };
  }

  return client.storage.from(DEMO_BUCKET).remove([path]);
}

export async function createObjectSignedUrl(
  session: AuthSession | null,
  path: string
): Promise<DomiBaseResponse<SignedUrl>> {
  const client = authenticatedClient(session);
  if (!client) {
    return { data: null, error: null, count: null };
  }

  return client.storage.from(DEMO_BUCKET).createSignedUrl(path, { expiresIn: 900 });
}

/** Demo function registered by scripts/dev/phase-06-demo.mjs. */
export const DEMO_FUNCTION = "demo-echo";

/**
 * Invokes the demo WASM function with the authenticated session. The guest echoes
 * the payload plus the server-derived context (projectId and userId), so the page
 * can show that the Runtime — not the client — supplies the identity.
 */
export async function invokeDemoFunction(
  session: AuthSession | null,
  payload: unknown
): Promise<DomiBaseResponse<unknown>> {
  const client = authenticatedClient(session);
  if (!client) {
    return { data: null, error: null, count: null };
  }

  return client.functions.invoke(DEMO_FUNCTION, payload);
}
