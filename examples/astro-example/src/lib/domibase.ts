import {
  createClient,
  type DomiBaseClient,
  type DomiBaseError,
  type DomiBaseResponse,
  type Row
} from "@domibase/client";
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
