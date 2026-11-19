import type { MigrationPlanRequest } from "./control-client";

/**
 * Typed migration planner for the Control API. It mirrors the server allowlists so the owner
 * sees errors before a privileged DDL transaction starts. It never produces SQL text: every
 * field becomes an allowlisted identifier, type, literal or policy template.
 */

export const MIN_VERSION = 1;
export const MAX_VERSION = 10_000;
export const MAX_OPERATIONS = 100;

export const MIGRATION_OPERATION_TYPES = [
  "create_table",
  "drop_table",
  "drop_column",
  "create_index",
  "drop_index",
  "create_policy",
  "alter_policy",
  "drop_policy"
] as const;

export type MigrationOperationType = (typeof MIGRATION_OPERATION_TYPES)[number];

export const MIGRATION_OPERATION_LABELS: Readonly<Record<MigrationOperationType, string>> = {
  create_table: "Crear tabla",
  drop_table: "Eliminar tabla",
  drop_column: "Eliminar columna",
  create_index: "Crear índice",
  drop_index: "Eliminar índice",
  create_policy: "Crear política",
  alter_policy: "Modificar política",
  drop_policy: "Eliminar política"
};

export const COLUMN_TYPES = [
  "uuid",
  "text",
  "int2",
  "int4",
  "int8",
  "boolean",
  "timestamptz",
  "timestamp",
  "date",
  "jsonb",
  "json",
  "numeric",
  "varchar"
] as const;

export const POLICY_COMMANDS = ["SELECT", "INSERT", "UPDATE", "DELETE"] as const;
export type PolicyCommand = (typeof POLICY_COMMANDS)[number];

export const POLICY_ROLES = ["anon", "authenticated"] as const;
export type PolicyRole = (typeof POLICY_ROLES)[number];

export const POLICY_EXPRESSIONS = [
  "true",
  "false",
  "project_id = current_setting('domibase.project_id', true)::uuid"
] as const;

export type DefaultMode = "none" | "gen_random_uuid()" | "now()" | "true" | "false" | "number" | "text";

export const DEFAULT_MODES: ReadonlyArray<{ readonly value: DefaultMode; readonly label: string }> = [
  { value: "none", label: "Sin valor" },
  { value: "gen_random_uuid()", label: "gen_random_uuid()" },
  { value: "now()", label: "now()" },
  { value: "true", label: "true" },
  { value: "false", label: "false" },
  { value: "number", label: "Número" },
  { value: "text", label: "Texto" }
];

export interface ColumnDraft {
  readonly name: string;
  readonly type: string;
  readonly varcharLength: string;
  readonly nullable: boolean;
  readonly primaryKey: boolean;
  readonly unique: boolean;
  readonly defaultMode: DefaultMode;
  readonly defaultValue: string;
  readonly referencesTable: string;
  readonly referencesColumn: string;
}

/**
 * A flat draft keeps the form editable. Only fields relevant to `type` are serialized.
 * `name` is the table for `create_table`, the index name for `create_index` and the policy
 * name for policy operations; `policy` is the existing policy name when altering or dropping.
 */
export interface OperationDraft {
  readonly type: MigrationOperationType;
  readonly name: string;
  readonly table: string;
  readonly column: string;
  readonly index: string;
  readonly policy: string;
  readonly columns: string;
  readonly unique: boolean;
  readonly columnDrafts: readonly ColumnDraft[];
  readonly commands: readonly PolicyCommand[];
  readonly roles: readonly PolicyRole[];
  readonly using: string;
  readonly withCheck: string;
}

export interface PlanBuildSuccess {
  readonly ok: true;
  readonly payload: MigrationPlanRequest;
}

export interface PlanBuildFailure {
  readonly ok: false;
  readonly errors: readonly string[];
}

export type PlanBuildResult = PlanBuildSuccess | PlanBuildFailure;

type SerializeSuccess = { readonly ok: true; readonly value: Record<string, unknown> };
type SerializeFailure = { readonly ok: false; readonly errors: readonly string[] };
type SerializeResult = SerializeSuccess | SerializeFailure;

const IDENTIFIER_PATTERN = /^[a-z_][a-z0-9_]*$/;
const VARCHAR_LENGTH_PATTERN = /^[1-9][0-9]{0,3}$/;
const NUMERIC_LITERAL_PATTERN = /^-?[0-9]{1,18}(\.[0-9]{1,9})?$/;
const UNSAFE_TEXT_PATTERN = /[\u0000-\u001f\\;\u007f]/;
const MAX_IDENTIFIER_LENGTH = 63;
const MAX_DEFAULT_LENGTH = 256;
const WELL_KNOWN_TYPES = new Set<string>(COLUMN_TYPES.filter((type) => type !== "varchar"));
const POLICY_COMMAND_SET = new Set<string>(POLICY_COMMANDS);
const POLICY_ROLE_SET = new Set<string>(POLICY_ROLES);
const POLICY_EXPRESSION_SET = new Set<string>(POLICY_EXPRESSIONS);

function trim(value: string): string {
  return value.trim();
}

export function isIdentifier(value: string): boolean {
  const candidate = trim(value);
  return (
    candidate.length > 0 &&
    candidate.length <= MAX_IDENTIFIER_LENGTH &&
    !candidate.startsWith("pg_") &&
    IDENTIFIER_PATTERN.test(candidate)
  );
}

function fail(message: string): SerializeFailure {
  return { ok: false, errors: [message] };
}

function resolveColumnType(draft: ColumnDraft): string | null {
  const type = trim(draft.type);
  if (type !== "varchar") {
    return WELL_KNOWN_TYPES.has(type) ? type : null;
  }

  const length = trim(draft.varcharLength);
  if (!VARCHAR_LENGTH_PATTERN.test(length) || Number(length) > 8000) {
    return null;
  }
  return `varchar(${length})`;
}

function resolveDefault(draft: ColumnDraft): { ok: true; value?: string | number | boolean } | SerializeFailure {
  switch (draft.defaultMode) {
    case "none":
      return { ok: true };
    case "gen_random_uuid()":
    case "now()":
      return { ok: true, value: draft.defaultMode };
    case "true":
      return { ok: true, value: true };
    case "false":
      return { ok: true, value: false };
    case "number": {
      const raw = trim(draft.defaultValue);
      if (!NUMERIC_LITERAL_PATTERN.test(raw) || !Number.isFinite(Number(raw))) {
        return fail("El valor por defecto numérico debe ser un decimal simple.");
      }
      return { ok: true, value: Number(raw) };
    }
    case "text": {
      const text = draft.defaultValue;
      if (text.length > MAX_DEFAULT_LENGTH || UNSAFE_TEXT_PATTERN.test(text)) {
        return fail("El valor por defecto de texto supera el límite o contiene caracteres no permitidos.");
      }
      return { ok: true, value: text };
    }
  }
}

function serializeColumn(draft: ColumnDraft): SerializeResult {
  if (!isIdentifier(draft.name)) {
    return fail(`El nombre de columna "${trim(draft.name)}" no es un identificador seguro.`);
  }

  const type = resolveColumnType(draft);
  if (type === null) {
    return fail(`El tipo de la columna "${trim(draft.name)}" no está permitido.`);
  }

  const column: Record<string, unknown> = {
    name: trim(draft.name),
    type,
    nullable: draft.nullable,
    primaryKey: draft.primaryKey,
    unique: draft.unique
  };

  const resolvedDefault = resolveDefault(draft);
  if (!resolvedDefault.ok) {
    return resolvedDefault;
  }
  if (resolvedDefault.value !== undefined) {
    column.default = resolvedDefault.value;
  }

  const referencesTable = trim(draft.referencesTable);
  const referencesColumn = trim(draft.referencesColumn);
  if (referencesTable.length > 0 || referencesColumn.length > 0) {
    if (!isIdentifier(referencesTable) || !isIdentifier(referencesColumn)) {
      return fail("La referencia de clave ajena necesita tabla y columna con identificadores seguros.");
    }
    column.references = { table: referencesTable, column: referencesColumn };
  }

  return { ok: true, value: column };
}

function splitIdentifiers(raw: string): string[] {
  return raw
    .split(",")
    .map((value) => trim(value))
    .filter((value) => value.length > 0);
}

function serializePolicyFields(draft: OperationDraft): SerializeResult {
  if (!isIdentifier(draft.table)) {
    return fail(`La tabla "${trim(draft.table)}" no es un identificador seguro.`);
  }

  const commands = [...draft.commands];
  if (commands.length === 0 || commands.length > 4 || new Set(commands).size !== commands.length) {
    return fail("Selecciona entre uno y cuatro comandos de política distintos.");
  }
  if (commands.some((command) => !POLICY_COMMAND_SET.has(command))) {
    return fail("Uno de los comandos de política no está permitido.");
  }

  const roles = [...draft.roles];
  if (roles.length === 0 || roles.length > 2 || new Set(roles).size !== roles.length) {
    return fail("Selecciona al menos un rol funcional sin repetirlo.");
  }
  if (roles.some((role) => !POLICY_ROLE_SET.has(role))) {
    return fail("Uno de los roles funcionales no está permitido.");
  }

  if (!POLICY_EXPRESSION_SET.has(trim(draft.using)) || !POLICY_EXPRESSION_SET.has(trim(draft.withCheck))) {
    return fail("Las expresiones de política deben salir del catálogo seguro del servidor.");
  }

  return { ok: true, value: { commands, roles, using: trim(draft.using), withCheck: trim(draft.withCheck) } };
}

export function serializeOperation(draft: OperationDraft): SerializeResult {
  switch (draft.type) {
    case "create_table": {
      if (!isIdentifier(draft.name)) {
        return fail(`El nombre de tabla "${trim(draft.name)}" no es un identificador seguro.`);
      }
      if (draft.columnDrafts.length === 0 || draft.columnDrafts.length > 100) {
        return fail("Una tabla necesita entre 1 y 100 columnas.");
      }

      const columns: Record<string, unknown>[] = [];
      for (const columnDraft of draft.columnDrafts) {
        const column = serializeColumn(columnDraft);
        if (!column.ok) {
          return column;
        }
        columns.push(column.value);
      }
      if (columns.filter((column) => column.primaryKey === true).length > 1) {
        return fail("Solo se admite una columna con clave primaria.");
      }

      return { ok: true, value: { type: "create_table", name: trim(draft.name), columns } };
    }

    case "drop_table":
      return isIdentifier(draft.table)
        ? { ok: true, value: { type: "drop_table", table: trim(draft.table) } }
        : fail(`La tabla "${trim(draft.table)}" no es un identificador seguro.`);

    case "drop_column":
      if (!isIdentifier(draft.table) || !isIdentifier(draft.column)) {
        return fail("La tabla y la columna deben ser identificadores seguros.");
      }
      return {
        ok: true,
        value: { type: "drop_column", table: trim(draft.table), column: trim(draft.column) }
      };

    case "create_index": {
      if (!isIdentifier(draft.table) || !isIdentifier(draft.name)) {
        return fail("La tabla y el índice deben ser identificadores seguros.");
      }
      const columns = splitIdentifiers(draft.columns);
      if (columns.length === 0 || columns.length > 16 || columns.some((column) => !isIdentifier(column))) {
        return fail("Un índice necesita entre 1 y 16 columnas seguras separadas por comas.");
      }
      return {
        ok: true,
        value: {
          type: "create_index",
          table: trim(draft.table),
          name: trim(draft.name),
          columns,
          unique: draft.unique
        }
      };
    }

    case "drop_index":
      if (!isIdentifier(draft.table) || !isIdentifier(draft.index)) {
        return fail("La tabla y el índice deben ser identificadores seguros.");
      }
      return {
        ok: true,
        value: { type: "drop_index", table: trim(draft.table), index: trim(draft.index) }
      };

    case "create_policy": {
      if (!isIdentifier(draft.name)) {
        return fail(`El nombre de política "${trim(draft.name)}" no es un identificador seguro.`);
      }
      const fields = serializePolicyFields(draft);
      if (!fields.ok) {
        return fields;
      }
      return {
        ok: true,
        value: { type: "create_policy", table: trim(draft.table), name: trim(draft.name), ...fields.value }
      };
    }

    case "alter_policy": {
      const target = trim(draft.policy);
      const policyName = trim(draft.name) || target;
      if (!isIdentifier(target) || !isIdentifier(policyName)) {
        return fail("La política actual y su nombre deben ser identificadores seguros.");
      }
      const fields = serializePolicyFields(draft);
      if (!fields.ok) {
        return fields;
      }
      return {
        ok: true,
        value: {
          type: "alter_policy",
          table: trim(draft.table),
          policy: target,
          name: policyName,
          ...fields.value
        }
      };
    }

    case "drop_policy":
      if (!isIdentifier(draft.table) || !isIdentifier(draft.policy)) {
        return fail("La tabla y la política deben ser identificadores seguros.");
      }
      return {
        ok: true,
        value: { type: "drop_policy", table: trim(draft.table), policy: trim(draft.policy) }
      };
  }

  return fail("Tipo de operación no soportado.");
}

export function buildMigrationPlan(
  versionInput: string | number,
  name: string,
  drafts: readonly OperationDraft[]
): PlanBuildResult {
  const errors: string[] = [];
  const rawVersion = typeof versionInput === "number" ? String(versionInput) : trim(versionInput);
  const version = Number(rawVersion);
  if (!/^[0-9]+$/.test(rawVersion) || !Number.isInteger(version) || version < MIN_VERSION || version > MAX_VERSION) {
    errors.push(`La versión debe ser un entero entre ${MIN_VERSION} y ${MAX_VERSION}.`);
  }

  const migrationName = trim(name);
  if (!isIdentifier(migrationName)) {
    errors.push("El nombre de la migración debe ser un identificador en minúsculas.");
  }

  if (drafts.length === 0 || drafts.length > MAX_OPERATIONS) {
    errors.push(`Una migración necesita entre 1 y ${MAX_OPERATIONS} operaciones.`);
  }

  const operations: Record<string, unknown>[] = [];
  drafts.forEach((draft, index) => {
    const result = serializeOperation(draft);
    if (result.ok) {
      operations.push(result.value);
    } else {
      errors.push(...result.errors.map((error) => `Operación ${index + 1}: ${error}`));
    }
  });

  if (errors.length > 0) {
    return { ok: false, errors };
  }
  return { ok: true, payload: { version, name: migrationName, operations } };
}

export function createColumnDraft(): ColumnDraft {
  return {
    name: "",
    type: "text",
    varcharLength: "255",
    nullable: true,
    primaryKey: false,
    unique: false,
    defaultMode: "none",
    defaultValue: "",
    referencesTable: "",
    referencesColumn: ""
  };
}

export function createOperationDraft(type: MigrationOperationType): OperationDraft {
  return {
    type,
    name: "",
    table: "",
    column: "",
    index: "",
    policy: "",
    columns: "",
    unique: false,
    columnDrafts: type === "create_table" ? [createColumnDraft()] : [],
    commands: ["SELECT"],
    roles: ["anon"],
    using: "true",
    withCheck: "true"
  };
}

export function suggestNextVersion(history: readonly { readonly version: number }[]): number {
  const highest = history.reduce((max, migration) => Math.max(max, migration.version), 0);
  return Math.min(MAX_VERSION, highest + 1);
}
