import { describe, expect, it } from "vitest";
import {
  buildMigrationPlan,
  createColumnDraft,
  createOperationDraft,
  serializeOperation,
  suggestNextVersion,
  type OperationDraft
} from "./migration-plan";

function draft(overrides: Partial<OperationDraft>): OperationDraft {
  return { ...createOperationDraft("drop_table"), ...overrides };
}

describe("migration planner", () => {
  it("serializes create_table with allowlisted types, defaults and references", () => {
    const operation: OperationDraft = {
      ...createOperationDraft("create_table"),
      name: "contactos",
      columnDrafts: [
        {
          ...createColumnDraft(),
          name: "id",
          type: "uuid",
          nullable: false,
          primaryKey: true,
          defaultMode: "gen_random_uuid()"
        },
        {
          ...createColumnDraft(),
          name: "estado",
          type: "varchar",
          varcharLength: "40",
          defaultMode: "text",
          defaultValue: "nuevo"
        },
        {
          ...createColumnDraft(),
          name: "proyecto_id",
          type: "uuid",
          referencesTable: "proyectos",
          referencesColumn: "id"
        }
      ]
    };

    const result = serializeOperation(operation);

    expect(result.ok).toBe(true);
    if (!result.ok) {
      return;
    }
    expect(result.value).toEqual({
      type: "create_table",
      name: "contactos",
      columns: [
        {
          name: "id",
          type: "uuid",
          nullable: false,
          primaryKey: true,
          unique: false,
          default: "gen_random_uuid()"
        },
        {
          name: "estado",
          type: "varchar(40)",
          nullable: true,
          primaryKey: false,
          unique: false,
          default: "nuevo"
        },
        {
          name: "proyecto_id",
          type: "uuid",
          nullable: true,
          primaryKey: false,
          unique: false,
          references: { table: "proyectos", column: "id" }
        }
      ]
    });
  });

  it("serializes booleans, numbers and the project-scoped policy template", () => {
    const column = serializeOperation({
      ...createOperationDraft("create_table"),
      name: "eventos",
      columnDrafts: [
        { ...createColumnDraft(), name: "activo", type: "boolean", defaultMode: "true" },
        { ...createColumnDraft(), name: "orden", type: "numeric", defaultMode: "number", defaultValue: "12.50" }
      ]
    });
    expect(column.ok).toBe(true);
    if (!column.ok) {
      return;
    }
    const columns = column.value.columns as Array<Record<string, unknown>>;
    expect(columns[0]?.default).toBe(true);
    expect(columns[1]?.default).toBe(12.5);

    const policy = serializeOperation(
      draft({
        type: "create_policy",
        table: "contactos",
        name: "contactos_select",
        commands: ["SELECT"],
        roles: ["anon"],
        using: "project_id = current_setting('domibase.project_id', true)::uuid",
        withCheck: "false"
      })
    );
    expect(policy.ok).toBe(true);
    if (!policy.ok) {
      return;
    }
    expect(policy.value).toEqual({
      type: "create_policy",
      table: "contactos",
      name: "contactos_select",
      commands: ["SELECT"],
      roles: ["anon"],
      using: "project_id = current_setting('domibase.project_id', true)::uuid",
      withCheck: "false"
    });
  });

  it("serializes index, drop and alter operations in the exact parser shape", () => {
    const index = serializeOperation(
      draft({ type: "create_index", table: "contactos", name: "contactos_email_idx", columns: "email, estado", unique: true })
    );
    expect(index.ok && index.value).toEqual({
      type: "create_index",
      table: "contactos",
      name: "contactos_email_idx",
      columns: ["email", "estado"],
      unique: true
    });

    const dropIndex = serializeOperation(
      draft({ type: "drop_index", table: "contactos", index: "contactos_email_idx" })
    );
    expect(dropIndex.ok && dropIndex.value).toEqual({
      type: "drop_index",
      table: "contactos",
      index: "contactos_email_idx"
    });

    const alter = serializeOperation(
      draft({
        type: "alter_policy",
        table: "contactos",
        policy: "contactos_select",
        name: "contactos_select_v2",
        commands: ["SELECT", "INSERT"],
        roles: ["anon", "authenticated"]
      })
    );
    expect(alter.ok && alter.value).toEqual({
      type: "alter_policy",
      table: "contactos",
      policy: "contactos_select",
      name: "contactos_select_v2",
      commands: ["SELECT", "INSERT"],
      roles: ["anon", "authenticated"],
      using: "true",
      withCheck: "true"
    });

    const dropPolicy = serializeOperation(
      draft({ type: "drop_policy", table: "contactos", policy: "contactos_select" })
    );
    expect(dropPolicy.ok && dropPolicy.value).toEqual({
      type: "drop_policy",
      table: "contactos",
      policy: "contactos_select"
    });
  });

  it("builds a complete plan with a numeric version", () => {
    const result = buildMigrationPlan("3", "add_contactos", [
      draft({ type: "create_table", name: "contactos", columnDrafts: [{ ...createColumnDraft(), name: "id", type: "uuid" }] }),
      draft({ type: "drop_table", table: "borrador" })
    ]);

    expect(result.ok).toBe(true);
    if (!result.ok) {
      return;
    }
    expect(result.payload).toEqual({
      version: 3,
      name: "add_contactos",
      operations: [
        {
          type: "create_table",
          name: "contactos",
          columns: [{ name: "id", type: "uuid", nullable: true, primaryKey: false, unique: false }]
        },
        { type: "drop_table", table: "borrador" }
      ]
    });
    expect(JSON.stringify(result.payload)).not.toContain("SELECT ");
    expect(JSON.stringify(result.payload)).not.toContain("DROP TABLE ");
  });

  it("suggests the next version from history", () => {
    expect(suggestNextVersion([{ version: 1 }, { version: 4 }])).toBe(5);
    expect(suggestNextVersion([])).toBe(1);
    expect(suggestNextVersion([{ version: 10_000 }])).toBe(10_000);
  });

  it("rejects unsafe identifiers before any request is sent", () => {
    const injection = buildMigrationPlan(1, "initial_schema", [
      draft({ type: "drop_table", table: 'contactos"; DROP TABLE proyecto; --' })
    ]);
    expect(injection.ok).toBe(false);
    if (!injection.ok) {
      expect(injection.errors.join(" ")).toContain("Operación 1");
    }

    const reserved = serializeOperation(draft({ type: "drop_table", table: "pg_hidden" }));
    expect(reserved.ok).toBe(false);

    const badName = buildMigrationPlan(1, "Initial Schema", [draft({ type: "drop_table", table: "contactos" })]);
    expect(badName.ok).toBe(false);
  });

  it("rejects unsupported types, defaults and varchar lengths", () => {
    const badType = serializeOperation({
      ...createOperationDraft("create_table"),
      name: "contactos",
      columnDrafts: [{ ...createColumnDraft(), name: "id", type: "text); DROP TABLE x; --" }]
    });
    expect(badType.ok).toBe(false);

    const badDefault = serializeOperation({
      ...createOperationDraft("create_table"),
      name: "contactos",
      columnDrafts: [{ ...createColumnDraft(), name: "estado", defaultMode: "text", defaultValue: "nuevo'; --" }]
    });
    expect(badDefault.ok).toBe(false);

    const badLength = serializeOperation({
      ...createOperationDraft("create_table"),
      name: "contactos",
      columnDrafts: [{ ...createColumnDraft(), name: "detalle", type: "varchar", varcharLength: "9000" }]
    });
    expect(badLength.ok).toBe(false);

    const badNumber = serializeOperation({
      ...createOperationDraft("create_table"),
      name: "contactos",
      columnDrafts: [{ ...createColumnDraft(), name: "orden", defaultMode: "number", defaultValue: "1e9" }]
    });
    expect(badNumber.ok).toBe(false);
  });

  it("rejects duplicate policy selections and unsafe expressions", () => {
    const duplicate = serializeOperation(
      draft({ type: "create_policy", table: "contactos", name: "contactos_policy", commands: ["SELECT", "SELECT"] })
    );
    expect(duplicate.ok).toBe(false);

    const expression = serializeOperation(
      draft({ type: "create_policy", table: "contactos", name: "contactos_policy", using: "true OR 1=1" })
    );
    expect(expression.ok).toBe(false);

    const roles = serializeOperation(
      draft({ type: "create_policy", table: "contactos", name: "contactos_policy", roles: [] })
    );
    expect(roles.ok).toBe(false);
  });

  it("enforces plan limits", () => {
    expect(buildMigrationPlan(0, "initial_schema", [draft({ type: "drop_table", table: "t" })])).toMatchObject({
      ok: false
    });
    expect(buildMigrationPlan(10_001, "initial_schema", [draft({ type: "drop_table", table: "t" })])).toMatchObject({
      ok: false
    });
    expect(buildMigrationPlan(1, "initial_schema", [])).toMatchObject({ ok: false });
    expect(
      buildMigrationPlan(
        1,
        "initial_schema",
        Array.from({ length: 101 }, () => draft({ type: "drop_table", table: "t" }))
      )
    ).toMatchObject({ ok: false });
  });
});
