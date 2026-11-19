/** Draft state and request mapping shared by table creation and column addition. */

export interface ColumnDraft {
  readonly key: number;
  name: string;
  type: string;
  nullable: boolean;
  primaryKey: boolean;
  unique: boolean;
  identity: boolean;
  defaultText: string;
}

export const sqlTypes = [
  "uuid", "text", "varchar(255)", "int4", "int8", "boolean",
  "timestamptz", "date", "jsonb", "numeric", "bytea", "inet"
] as const;

const identityTypes = new Set<string>(["int4", "int8"]);

/** PostgreSQL IDENTITY is only valid on integer types. */
export function supportsIdentity(type: string): boolean {
  return identityTypes.has(type);
}

export function withType(column: ColumnDraft, type: string): ColumnDraft {
  return { ...column, type, identity: supportsIdentity(type) ? column.identity : false };
}

let nextDraftKey = 1;

export function blankColumn(): ColumnDraft {
  return {
    key: nextDraftKey++,
    name: "",
    type: "text",
    nullable: true,
    primaryKey: false,
    unique: false,
    identity: false,
    defaultText: ""
  };
}

/** IDENTITY and DEFAULT are mutually exclusive, so only one of them reaches the API. */
export function columnRequest(column: ColumnDraft): Record<string, unknown> {
  const request: Record<string, unknown> = {
    name: column.name.trim(),
    type: column.type,
    nullable: column.nullable,
    primaryKey: column.primaryKey,
    unique: column.unique
  };
  if (column.identity) {
    request.identity = true;
  } else if (column.defaultText.trim() !== "") {
    request.default = column.defaultText.trim();
  }
  return request;
}
