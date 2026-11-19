export type ProjectStatus = "Provisioning" | "Ready" | "Failed" | "Deleting";

export interface Project {
  readonly id: string;
  readonly slug: string;
  readonly name: string;
  readonly status: ProjectStatus | string;
  readonly databaseName: string;
  readonly failureReason: string | null;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly rateLimitPermits: number | null;
  readonly rateLimitWindowSeconds: number | null;
  readonly effectiveRateLimitPermits: number;
  readonly effectiveRateLimitWindowSeconds: number;
}

export type ApiKeyKind = "Public" | "Secret";

export interface ApiKey {
  readonly id: string;
  readonly projectId: string;
  readonly kind: ApiKeyKind | string;
  readonly prefix: string;
  readonly label: string | null;
  readonly createdAt: string;
  readonly revokedAt: string | null;
  readonly lastUsedAt: string | null;
}

/** Contains the full key value. Exists only in memory, right after creation. */
export interface CreatedApiKey {
  readonly id: string;
  readonly projectId: string;
  readonly kind: ApiKeyKind | string;
  readonly prefix: string;
  readonly key: string;
  readonly label: string | null;
  readonly createdAt: string;
}

export interface Bucket {
  readonly id: string;
  readonly name: string;
  readonly publicAccess: boolean;
  readonly createdAt: string;
}

export interface FunctionDefinition {
  readonly id: string;
  readonly name: string;
  readonly description: string | null;
  readonly moduleSha256: string;
  readonly enabled: boolean;
  readonly fuel: number;
  readonly memoryPages: number;
  readonly timeoutMs: number;
  readonly maxInputBytes: number;
  readonly maxOutputBytes: number;
  readonly createdAt: string;
}

/** Live catalog objects from the `public` schema of a project database. */
export interface SchemaTable {
  readonly name: string;
}

export interface SchemaView {
  readonly name: string;
}

export interface SchemaFunction {
  readonly name: string;
  readonly arguments: string;
  readonly returnType: string;
}

export interface SchemaColumn {
  readonly name: string;
  readonly type: string;
  readonly dataType: string;
  readonly nullable: boolean;
  readonly default: string | null;
  readonly primaryKey: boolean;
  readonly unique: boolean;
  readonly identity: boolean;
  readonly foreignTable: string | null;
  readonly foreignColumn: string | null;
}

export interface ColumnRequest {
  readonly name: string;
  readonly type: string;
  readonly nullable?: boolean;
  readonly primaryKey?: boolean;
  readonly unique?: boolean;
  readonly default?: unknown;
  readonly references?: { readonly table: string; readonly column: string };
  readonly identity?: boolean;
}

export interface CreateTableRequest {
  readonly name: string;
  readonly columns: readonly ColumnRequest[];
}

export interface DataEditResult {
  readonly affected: number;
  readonly rows: readonly Record<string, unknown>[];
}

/**
 * Administrative row preview. It runs through the Runtime path with the anonymous
 * functional role; the client never supplies the role, database, key or SQL.
 */
export interface DataPreviewRequest {
  readonly select?: string;
  readonly filters?: readonly string[];
  readonly order?: string;
  readonly limit?: number;
  readonly offset?: number;
}

export interface DataPreviewResult {
  /** Always `anon`; reported by the server so the panel cannot misrepresent the context. */
  readonly role: string;
  readonly columns: readonly string[];
  readonly rows: readonly Record<string, unknown>[];
  readonly rowCount: number;
  readonly truncated: boolean;
}

export type PolicyCommand = "SELECT" | "INSERT" | "UPDATE" | "DELETE";

export type PolicyRole = "anon" | "authenticated";

/** The backend accepts only these fixed expressions; free-form SQL is never allowed. */
export type PolicyExpression =
  | "true"
  | "false"
  | "project_id = current_setting('domibase.project_id', true)::uuid";

export interface RlsPolicy {
  readonly name: string;
  readonly commands: readonly string[];
  readonly roles: readonly string[];
  readonly using: string | null;
  readonly withCheck: string | null;
}

export interface PolicyRequest {
  readonly name: string;
  readonly commands: readonly PolicyCommand[];
  readonly roles: readonly PolicyRole[];
  readonly using?: PolicyExpression;
  readonly withCheck?: PolicyExpression;
}

export interface SchemaMutationResult {
  readonly action: string;
  readonly table: string;
  readonly object: string | null;
}

/** Bounded request for the administrative read-only SQL editor. */
export interface SqlQueryRequest {
  readonly sql: string;
  readonly maxRows?: number;
  readonly timeoutMs?: number;
}

/** Already serialized rows; values are JSON-safe PostgreSQL values. */
export interface SqlQueryResult {
  readonly columns: readonly string[];
  readonly rows: readonly Record<string, unknown>[];
  readonly rowCount: number;
  readonly truncated: boolean;
  readonly durationMs: number;
}

/** Live state of an allowlisted PostgreSQL extension in the active project. */
export interface PostgresExtension {
  readonly name: string;
  readonly description: string;
  readonly trusted: boolean;
  readonly availableVersion: string | null;
  readonly installedVersion: string | null;
  readonly available: boolean;
  readonly installed: boolean;
  readonly canInstall: boolean;
  readonly canUninstall: boolean;
}

export interface ExtensionMutationResult {
  readonly name: string;
  readonly action: string;
}

/** Logical dump metadata. The server never exposes filesystem paths or credentials. */
export interface ProjectBackup {
  readonly id: string;
  readonly projectId: string;
  readonly format: string;
  readonly sizeBytes: number;
  readonly sha256: string;
  readonly createdAt: string;
}

export interface BackupRestoreResult {
  readonly backupId: string;
  readonly projectId: string;
  readonly status: string;
}

/** A migration already recorded in the private history of a project database. */
export interface AppliedMigration {
  readonly version: number;
  readonly name: string;
  readonly checksum: string;
  readonly operationCount: number;
  readonly appliedAt: string;
}

/** Read-only administrative audit row. Details are redacted server-side before transport. */
export interface AuditEntry {
  readonly id: string;
  readonly occurredAt: string;
  readonly actor: string;
  readonly action: string;
  readonly targetType: string;
  readonly targetId: string | null;
  readonly projectId: string | null;
  readonly details: string | null;
  readonly correlationId: string | null;
}

export interface AuditQuery {
  readonly projectId?: string;
  readonly action?: string;
  readonly actor?: string;
  readonly targetType?: string;
  readonly from?: string;
  readonly to?: string;
  readonly limit?: number;
  readonly offset?: number;
}

const AuditQueryKeys = [
  "projectId",
  "action",
  "actor",
  "targetType",
  "from",
  "to",
  "limit",
  "offset"
] as const;

export interface AuditPage {
  readonly items: readonly AuditEntry[];
  readonly total: number;
  readonly limit: number;
  readonly offset: number;
}

/** One recorded provisioning step; errors are redacted by the server. */
export interface ProvisioningOperation {
  readonly id: string;
  readonly projectId: string;
  readonly step: string;
  readonly status: string;
  readonly attempt: number;
  readonly error: string | null;
  readonly correlationId: string | null;
  readonly startedAt: string;
  readonly completedAt: string | null;
}

/** Non-secret confirmation returned after applying a migration. */
export interface MigrationSummary {
  readonly version: number;
  readonly name: string;
  readonly checksum: string;
  readonly operationCount: number;
  readonly alreadyApplied: boolean;
}

/** Typed plan accepted by the Control Plane. It never contains SQL text. */
export interface MigrationPlanRequest {
  readonly version: number;
  readonly name: string;
  readonly operations: readonly Record<string, unknown>[];
}

export class ControlApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly details?: string;
  readonly hint?: string;

  constructor(code: string, message: string, status: number, details?: string, hint?: string) {
    super(message);
    this.name = "ControlApiError";
    this.code = code;
    this.status = status;
    if (details !== undefined) {
      this.details = details;
    }
    if (hint !== undefined) {
      this.hint = hint;
    }
  }
}

export type FetchLike = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

export interface ControlClientOptions {
  readonly baseUrl?: string;
  readonly loginUrl?: string;
  readonly apiUrl?: string;
  readonly token?: string;
  readonly fetch?: FetchLike;
}

/** Short-lived session issued by /admin/v1/login. */
export interface AdminLoginResponse {
  readonly token: string;
  readonly expiresAt: string;
}

interface ErrorEnvelope {
  readonly error?: {
    readonly code?: unknown;
    readonly message?: unknown;
    readonly details?: unknown;
    readonly hint?: unknown;
  };
}

function browserOriginOr(fallback: string): string {
  return typeof location !== "undefined" && location.origin ? location.origin : fallback;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function asOptionalString(value: unknown): string | undefined {
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

function fallbackError(status: number): ControlApiError {
  if (status === 401) {
    return new ControlApiError(
      "UNAUTHORIZED",
      "La sesión no es válida o ha caducado.",
      status
    );
  }
  if (status === 403) {
    return new ControlApiError("FORBIDDEN", "Esta sesión no permite la operación.", status);
  }
  if (status >= 500) {
    return new ControlApiError("SERVER_ERROR", "La API no pudo completar la operación.", status);
  }
  return new ControlApiError("REQUEST_FAILED", "La petición no se pudo completar.", status);
}

function toError(payload: unknown, status: number): ControlApiError {
  const fallback = fallbackError(status);
  if (!isRecord(payload) || !isRecord((payload as ErrorEnvelope).error)) {
    return fallback;
  }

  const envelope = (payload as ErrorEnvelope).error;
  if (!envelope) {
    return fallback;
  }

  const code = asOptionalString(envelope.code) ?? fallback.code;
  const message = asOptionalString(envelope.message) ?? fallback.message;
  return new ControlApiError(
    code,
    message,
    status,
    asOptionalString(envelope.details),
    asOptionalString(envelope.hint)
  );
}

/**
 * HTTP client for the private Control Plane. The token is kept only in memory;
 * callers decide where it comes from. Fetch is injectable for tests.
 */
export class ControlClient {
  private readonly baseUrl: string;
  private readonly loginUrl: string;
  private readonly apiUrlValue: string;
  private readonly fetchImpl: FetchLike;
  private tokenValue: string | null;

  constructor(options: ControlClientOptions = {}) {
    this.baseUrl = (options.baseUrl ?? "/api/v1").replace(/\/+$/, "");
    const apiRoot = this.baseUrl.replace(/\/api\/v1$/, "");
    this.loginUrl = (options.loginUrl ?? `${apiRoot}/admin/v1/login`).replace(/\/+$/, "");
    this.apiUrlValue = (
      options.apiUrl ??
      (apiRoot.startsWith("http") ? apiRoot : browserOriginOr("http://localhost:5080"))
    ).replace(/\/+$/, "");
    this.fetchImpl = options.fetch ?? ((input, init) => globalThis.fetch(input, init));
    this.tokenValue = options.token && options.token.length > 0 ? options.token : null;
  }

  get token(): string | null {
    return this.tokenValue;
  }

  /** Public Runtime base URL shown in the Connection screen. */
  get apiUrl(): string {
    return this.apiUrlValue;
  }

  setToken(token: string | null): void {
    this.tokenValue = token && token.length > 0 ? token : null;
  }

  private async request<T>(path: string, init: RequestInit = {}, signal?: AbortSignal): Promise<T> {
    if (!this.tokenValue) {
      throw new ControlApiError(
        "NO_SESSION",
        "Inicia sesión para continuar.",
        401
      );
    }

    const headers = new Headers(init.headers);
    headers.set("X-DomiBase-Admin-Token", this.tokenValue);
    if (init.body !== undefined && !headers.has("Content-Type")) {
      headers.set("Content-Type", "application/json");
    }

    const requestInit: RequestInit = { ...init, headers };
    const resolvedSignal = signal ?? init.signal;
    if (resolvedSignal !== undefined) {
      requestInit.signal = resolvedSignal;
    }

    let response: Response;
    try {
      response = await this.fetchImpl(`${this.baseUrl}${path}`, requestInit);
    } catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") {
        throw error;
      }
      throw new ControlApiError(
        "NETWORK_ERROR",
        "No se pudo conectar con la API de control.",
        0
      );
    }

    if (response.status === 204) {
      return undefined as T;
    }

    const text = await response.text();
    let payload: unknown = null;
    if (text.length > 0) {
      try {
        payload = JSON.parse(text) as unknown;
      } catch {
        throw new ControlApiError(
          "INVALID_RESPONSE",
          "La API devolvió una respuesta no válida.",
          response.status
        );
      }
    }

    if (!response.ok) {
      throw toError(payload, response.status);
    }

    return payload as T;
  }

  /** Exchanges owner credentials for a short-lived session token. */
  async login(username: string, password: string, signal?: AbortSignal): Promise<AdminLoginResponse> {
    const headers = new Headers({ "Content-Type": "application/json" });
    let response: Response;
    try {
      const init: RequestInit = {
        method: "POST",
        headers,
        body: JSON.stringify({ username, password })
      };
      if (signal !== undefined) {
        init.signal = signal;
      }
      response = await this.fetchImpl(this.loginUrl, init);
    } catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") {
        throw error;
      }
      throw new ControlApiError("NETWORK_ERROR", "No se pudo conectar con la API de control.", 0);
    }

    const text = await response.text();
    let payload: unknown = null;
    if (text.length > 0) {
      try {
        payload = JSON.parse(text) as unknown;
      } catch {
        throw new ControlApiError(
          "INVALID_RESPONSE",
          "La API devolvió una respuesta no válida.",
          response.status
        );
      }
    }

    if (!response.ok) {
      throw toError(payload, response.status);
    }

    if (!isRecord(payload) || typeof payload.token !== "string" || payload.token.length === 0) {
      throw new ControlApiError(
        "INVALID_RESPONSE",
        "La API no devolvió una sesión válida.",
        response.status
      );
    }

    return {
      token: payload.token,
      expiresAt: typeof payload.expiresAt === "string" ? payload.expiresAt : ""
    };
  }

  listProjects(signal?: AbortSignal): Promise<Project[]> {
    return this.request<Project[]>("/projects", {}, signal);
  }

  getProject(id: string, signal?: AbortSignal): Promise<Project> {
    return this.request<Project>(`/projects/${encodeURIComponent(id)}`, {}, signal);
  }

  createProject(input: { slug: string; name: string }, signal?: AbortSignal): Promise<Project> {
    return this.request<Project>("/projects", {
      method: "POST",
      body: JSON.stringify(input)
    }, signal);
  }

  deleteProject(id: string, confirmSlug: string, signal?: AbortSignal): Promise<void> {
    return this.request<void>(
      `/projects/${encodeURIComponent(id)}`,
      {
        method: "DELETE",
        headers: { "X-DomiBase-Confirm-Delete": "delete" },
        body: JSON.stringify({ confirmSlug })
      },
      signal
    );
  }

  retryProvisioning(id: string, signal?: AbortSignal): Promise<Project> {
    return this.request<Project>(`/projects/${encodeURIComponent(id)}/provision`, {
      method: "POST"
    }, signal);
  }

  updateRateLimit(
    id: string,
    input: { permits: number | null; windowSeconds: number | null },
    signal?: AbortSignal
  ): Promise<Project> {
    return this.request<Project>(`/projects/${encodeURIComponent(id)}/rate-limit`, {
      method: "PATCH",
      body: JSON.stringify(input)
    }, signal);
  }

  listKeys(projectId: string, signal?: AbortSignal): Promise<ApiKey[]> {
    return this.request<ApiKey[]>(`/projects/${encodeURIComponent(projectId)}/keys`, {}, signal);
  }

  createKey(
    projectId: string,
    input: { kind: ApiKeyKind; label?: string },
    signal?: AbortSignal
  ): Promise<CreatedApiKey> {
    return this.request<CreatedApiKey>(`/projects/${encodeURIComponent(projectId)}/keys`, {
      method: "POST",
      body: JSON.stringify(input)
    }, signal);
  }

  revokeKey(projectId: string, keyId: string, signal?: AbortSignal): Promise<void> {
    return this.request<void>(
      `/projects/${encodeURIComponent(projectId)}/keys/${encodeURIComponent(keyId)}`,
      { method: "DELETE" },
      signal
    );
  }

  listBuckets(projectId: string, signal?: AbortSignal): Promise<Bucket[]> {
    return this.request<Bucket[]>(`/projects/${encodeURIComponent(projectId)}/buckets`, {}, signal);
  }

  listFunctions(projectId: string, signal?: AbortSignal): Promise<FunctionDefinition[]> {
    return this.request<FunctionDefinition[]>(
      `/projects/${encodeURIComponent(projectId)}/functions`,
      {},
      signal
    );
  }

  listSchemaTables(projectId: string, signal?: AbortSignal): Promise<SchemaTable[]> {
    return this.request<SchemaTable[]>(
      `/projects/${encodeURIComponent(projectId)}/schema/tables`,
      {},
      signal
    );
  }

  listSchemaViews(projectId: string, signal?: AbortSignal): Promise<SchemaView[]> {
    return this.request<SchemaView[]>(
      `/projects/${encodeURIComponent(projectId)}/schema/views`,
      {},
      signal
    );
  }

  listSchemaFunctions(projectId: string, signal?: AbortSignal): Promise<SchemaFunction[]> {
    return this.request<SchemaFunction[]>(
      `/projects/${encodeURIComponent(projectId)}/schema/functions`,
      {},
      signal
    );
  }

  listSchemaColumns(projectId: string, table: string, signal?: AbortSignal): Promise<SchemaColumn[]> {
    return this.request<SchemaColumn[]>(
      `/projects/${encodeURIComponent(projectId)}/schema/tables/${encodeURIComponent(table)}/columns`,
      {},
      signal
    );
  }

  createTable(
    projectId: string,
    input: CreateTableRequest,
    signal?: AbortSignal
  ): Promise<SchemaMutationResult> {
    return this.request<SchemaMutationResult>(
      `/projects/${encodeURIComponent(projectId)}/schema/tables`,
      { method: "POST", body: JSON.stringify(input) },
      signal
    );
  }

  dropTable(projectId: string, table: string, signal?: AbortSignal): Promise<SchemaMutationResult> {
    return this.request<SchemaMutationResult>(
      `/projects/${encodeURIComponent(projectId)}/schema/tables/${encodeURIComponent(table)}`,
      { method: "DELETE" },
      signal
    );
  }

  addColumn(
    projectId: string,
    table: string,
    input: ColumnRequest,
    signal?: AbortSignal
  ): Promise<SchemaMutationResult> {
    return this.request<SchemaMutationResult>(
      `/projects/${encodeURIComponent(projectId)}/schema/tables/${encodeURIComponent(table)}/columns`,
      { method: "POST", body: JSON.stringify(input) },
      signal
    );
  }

  dropColumn(
    projectId: string,
    table: string,
    column: string,
    signal?: AbortSignal
  ): Promise<SchemaMutationResult> {
    return this.request<SchemaMutationResult>(
      `/projects/${encodeURIComponent(projectId)}/schema/tables/${encodeURIComponent(table)}/columns/${encodeURIComponent(column)}`,
      { method: "DELETE" },
      signal
    );
  }

  insertRows(
    projectId: string,
    table: string,
    rows: readonly Record<string, unknown>[],
    signal?: AbortSignal
  ): Promise<DataEditResult> {
    return this.request<DataEditResult>(
      this.rowsPath(projectId, table),
      { method: "POST", body: JSON.stringify(rows) },
      signal
    );
  }

  updateRows(
    projectId: string,
    table: string,
    values: Record<string, unknown>,
    filters: readonly string[],
    signal?: AbortSignal
  ): Promise<DataEditResult> {
    return this.request<DataEditResult>(
      this.rowsPath(projectId, table, filters),
      { method: "PATCH", body: JSON.stringify(values) },
      signal
    );
  }

  deleteRows(
    projectId: string,
    table: string,
    filters: readonly string[],
    signal?: AbortSignal
  ): Promise<DataEditResult> {
    return this.request<DataEditResult>(
      this.rowsPath(projectId, table, filters),
      { method: "DELETE" },
      signal
    );
  }

  private rowsPath(projectId: string, table: string, filters: readonly string[] = []): string {
    const params = new URLSearchParams();
    for (const filter of filters) {
      if (filter) {
        params.append("f", filter);
      }
    }
    const suffix = params.size > 0 ? `?${params.toString()}` : "";
    return `/projects/${encodeURIComponent(projectId)}/data/tables/${encodeURIComponent(table)}/rows${suffix}`;
  }

  /**
   * Reads rows with real RLS and column grants. Only `select`, `f`, `order`, `limit` and
   * `offset` are forwarded; the server validates every identifier and bounds every value.
   */
  previewTableRows(
    projectId: string,
    table: string,
    input: DataPreviewRequest = {},
    signal?: AbortSignal
  ): Promise<DataPreviewResult> {
    const params = new URLSearchParams();
    if (input.select !== undefined && input.select !== "") {
      params.set("select", input.select);
    }
    for (const filter of input.filters ?? []) {
      if (filter !== "") {
        params.append("f", filter);
      }
    }
    if (input.order !== undefined && input.order !== "") {
      params.set("order", input.order);
    }
    if (input.limit !== undefined) {
      params.set("limit", String(input.limit));
    }
    if (input.offset !== undefined) {
      params.set("offset", String(input.offset));
    }
    const suffix = params.size > 0 ? `?${params.toString()}` : "";
    return this.request<DataPreviewResult>(
      `/projects/${encodeURIComponent(projectId)}/data/tables/${encodeURIComponent(table)}/rows${suffix}`,
      {},
      signal
    );
  }

  listPolicies(projectId: string, table: string, signal?: AbortSignal): Promise<RlsPolicy[]> {
    return this.request<RlsPolicy[]>(
      `/projects/${encodeURIComponent(projectId)}/schema/tables/${encodeURIComponent(table)}/policies`,
      {},
      signal
    );
  }

  createPolicy(
    projectId: string,
    table: string,
    input: PolicyRequest,
    signal?: AbortSignal
  ): Promise<SchemaMutationResult> {
    return this.request<SchemaMutationResult>(
      `/projects/${encodeURIComponent(projectId)}/schema/tables/${encodeURIComponent(table)}/policies`,
      { method: "POST", body: JSON.stringify(input) },
      signal
    );
  }

  updatePolicy(
    projectId: string,
    table: string,
    policy: string,
    input: PolicyRequest,
    signal?: AbortSignal
  ): Promise<SchemaMutationResult> {
    return this.request<SchemaMutationResult>(
      `/projects/${encodeURIComponent(projectId)}/schema/tables/${encodeURIComponent(table)}/policies/${encodeURIComponent(policy)}`,
      { method: "PATCH", body: JSON.stringify(input) },
      signal
    );
  }

  deletePolicy(
    projectId: string,
    table: string,
    policy: string,
    signal?: AbortSignal
  ): Promise<void> {
    return this.request<void>(
      `/projects/${encodeURIComponent(projectId)}/schema/tables/${encodeURIComponent(table)}/policies/${encodeURIComponent(policy)}`,
      { method: "DELETE" },
      signal
    );
  }

  runSqlQuery(projectId: string, input: SqlQueryRequest, signal?: AbortSignal): Promise<SqlQueryResult> {
    return this.request<SqlQueryResult>(`/projects/${encodeURIComponent(projectId)}/sql/query`, {
      method: "POST",
      body: JSON.stringify(input)
    }, signal);
  }

  listExtensions(projectId: string, signal?: AbortSignal): Promise<PostgresExtension[]> {
    return this.request<PostgresExtension[]>(
      `/projects/${encodeURIComponent(projectId)}/extensions`,
      {},
      signal
    );
  }

  installExtension(projectId: string, name: string, signal?: AbortSignal): Promise<ExtensionMutationResult> {
    return this.request<ExtensionMutationResult>(`/projects/${encodeURIComponent(projectId)}/extensions`, {
      method: "POST",
      body: JSON.stringify({ name })
    }, signal);
  }

  uninstallExtension(projectId: string, name: string, signal?: AbortSignal): Promise<ExtensionMutationResult> {
    return this.request<ExtensionMutationResult>(
      `/projects/${encodeURIComponent(projectId)}/extensions/${encodeURIComponent(name)}`,
      { method: "DELETE" },
      signal
    );
  }

  listBackups(projectId: string, signal?: AbortSignal): Promise<ProjectBackup[]> {
    return this.request<ProjectBackup[]>(`/projects/${encodeURIComponent(projectId)}/backups`, {}, signal);
  }

  createBackup(projectId: string, signal?: AbortSignal): Promise<ProjectBackup> {
    return this.request<ProjectBackup>(`/projects/${encodeURIComponent(projectId)}/backups`, {
      method: "POST"
    }, signal);
  }

  /** Streams the dump as an opaque blob; the admin token is sent as a header, never in the URL. */
  async downloadBackup(projectId: string, backupId: string, signal?: AbortSignal): Promise<Blob> {
    if (!this.tokenValue) {
      throw new ControlApiError("NO_SESSION", "Inicia sesión para continuar.", 401);
    }

    const headers = new Headers();
    headers.set("X-DomiBase-Admin-Token", this.tokenValue);
    const url =
      `${this.baseUrl}/projects/${encodeURIComponent(projectId)}/backups/${encodeURIComponent(backupId)}/download`;
    const init: RequestInit = { headers };
    if (signal !== undefined) {
      init.signal = signal;
    }

    let response: Response;
    try {
      response = await this.fetchImpl(url, init);
    } catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") {
        throw error;
      }
      throw new ControlApiError("NETWORK_ERROR", "No se pudo conectar con la API de control.", 0);
    }

    if (!response.ok) {
      const text = await response.text();
      let payload: unknown = null;
      if (text.length > 0) {
        try {
          payload = JSON.parse(text) as unknown;
        } catch {
          // Keep the canonical error mapping below.
        }
      }
      throw toError(payload, response.status);
    }

    return response.blob();
  }

  deleteBackup(projectId: string, backupId: string, signal?: AbortSignal): Promise<void> {
    return this.request<void>(
      `/projects/${encodeURIComponent(projectId)}/backups/${encodeURIComponent(backupId)}`,
      { method: "DELETE" },
      signal
    );
  }

  /** The confirmation must be the exact project slug; the server refuses any other value. */
  restoreBackup(
    projectId: string,
    backupId: string,
    confirmSlug: string,
    signal?: AbortSignal
  ): Promise<BackupRestoreResult> {
    const headers = new Headers();
    headers.set("X-DomiBase-Confirm", confirmSlug);
    return this.request<BackupRestoreResult>(
      `/projects/${encodeURIComponent(projectId)}/backups/${encodeURIComponent(backupId)}/restore`,
      { method: "POST", headers },
      signal
    );
  }

  /** Read-only audit viewer. Only allowlisted query keys are sent to the server. */
  listAudit(query: AuditQuery = {}, signal?: AbortSignal): Promise<AuditPage> {
    const params = new URLSearchParams();
    for (const key of AuditQueryKeys) {
      const value = query[key];
      if (value !== undefined && value !== null && value !== "") {
        params.set(key, String(value));
      }
    }
    const suffix = params.size > 0 ? `?${params.toString()}` : "";
    return this.request<AuditPage>(`/audit${suffix}`, {}, signal);
  }

  listProvisioningOperations(projectId: string, signal?: AbortSignal): Promise<ProvisioningOperation[]> {
    return this.request<ProvisioningOperation[]>(
      `/projects/${encodeURIComponent(projectId)}/provisioning-operations`,
      {},
      signal
    );
  }

  listMigrations(projectId: string, signal?: AbortSignal): Promise<AppliedMigration[]> {
    return this.request<AppliedMigration[]>(
      `/projects/${encodeURIComponent(projectId)}/migrations`,
      {},
      signal
    );
  }

  applyMigration(
    projectId: string,
    plan: MigrationPlanRequest,
    signal?: AbortSignal
  ): Promise<MigrationSummary> {
    return this.request<MigrationSummary>(`/projects/${encodeURIComponent(projectId)}/migrations`, {
      method: "POST",
      body: JSON.stringify(plan)
    }, signal);
  }
}
