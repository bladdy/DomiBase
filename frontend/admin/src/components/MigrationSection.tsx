import { useEffect, useMemo, useState } from "react";
import type {
  AppliedMigration,
  ControlClient,
  MigrationPlanRequest,
  MigrationSummary,
  Project
} from "../lib/control-client";
import { errorText, formatDate } from "../lib/format";
import {
  buildMigrationPlan,
  createColumnDraft,
  createOperationDraft,
  MIGRATION_OPERATION_LABELS,
  MIGRATION_OPERATION_TYPES,
  POLICY_COMMANDS,
  POLICY_EXPRESSIONS,
  POLICY_ROLES,
  suggestNextVersion,
  type ColumnDraft,
  type MigrationOperationType,
  type OperationDraft,
  type PolicyCommand,
  type PolicyRole
} from "../lib/migration-plan";
import { ConfirmDialog } from "./ConfirmDialog";
import { Notice } from "./Notice";

interface MigrationSectionProps {
  readonly client: ControlClient;
  readonly project: Project | null;
  readonly migrations: readonly AppliedMigration[];
  readonly loading: boolean;
  readonly error: string | null;
  readonly onChanged: () => void;
}

export function MigrationSection({
  client,
  project,
  migrations,
  loading,
  error,
  onChanged
}: MigrationSectionProps) {
  const [version, setVersion] = useState(() => String(suggestNextVersion(migrations)));
  const [name, setName] = useState("");
  const [drafts, setDrafts] = useState<OperationDraft[]>(() => [createOperationDraft("create_table")]);
  const [newType, setNewType] = useState<MigrationOperationType>("create_table");
  const [validation, setValidation] = useState<readonly string[]>([]);
  const [pendingPlan, setPendingPlan] = useState<MigrationPlanRequest | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [applying, setApplying] = useState(false);
  const [applyError, setApplyError] = useState<string | null>(null);
  const [result, setResult] = useState<MigrationSummary | null>(null);

  const built = useMemo(() => buildMigrationPlan(version, name, drafts), [version, name, drafts]);

  useEffect(() => {
    setVersion(String(suggestNextVersion(migrations)));
  }, [migrations]);

  function clearFeedback(): void {
    setValidation([]);
    setPendingPlan(null);
    setApplyError(null);
    setResult(null);
  }

  function patchOperation(index: number, patch: Partial<OperationDraft>): void {
    clearFeedback();
    setDrafts((current) =>
      current.map((item, position) => (position === index ? { ...item, ...patch } : item))
    );
  }

  function patchColumn(operationIndex: number, columnIndex: number, patch: Partial<ColumnDraft>): void {
    clearFeedback();
    setDrafts((current) =>
      current.map((item, position) => {
        if (position !== operationIndex) {
          return item;
        }
        return {
          ...item,
          columnDrafts: item.columnDrafts.map((column, index) =>
            index === columnIndex ? { ...column, ...patch } : column
          )
        };
      })
    );
  }

  function addColumn(operationIndex: number): void {
    clearFeedback();
    setDrafts((current) =>
      current.map((item, position) =>
        position === operationIndex
          ? { ...item, columnDrafts: [...item.columnDrafts, createColumnDraft()] }
          : item
      )
    );
  }

  function removeColumn(operationIndex: number, columnIndex: number): void {
    clearFeedback();
    setDrafts((current) =>
      current.map((item, position) =>
        position === operationIndex
          ? { ...item, columnDrafts: item.columnDrafts.filter((_, index) => index !== columnIndex) }
          : item
      )
    );
  }

  function toggleCommand(index: number, command: PolicyCommand): void {
    const current = drafts[index]?.commands ?? [];
    patchOperation(index, {
      commands: current.includes(command) ? current.filter((item) => item !== command) : [...current, command]
    });
  }

  function toggleRole(index: number, role: PolicyRole): void {
    const current = drafts[index]?.roles ?? [];
    patchOperation(index, {
      roles: current.includes(role) ? current.filter((item) => item !== role) : [...current, role]
    });
  }

  function addOperation(): void {
    clearFeedback();
    setDrafts((current) => [...current, createOperationDraft(newType)]);
  }

  function removeOperation(index: number): void {
    clearFeedback();
    setDrafts((current) => current.filter((_, position) => position !== index));
  }

  function review(): void {
    if (!built.ok) {
      setValidation(built.errors);
      setPendingPlan(null);
      return;
    }
    setValidation([]);
    setApplyError(null);
    setPendingPlan(built.payload);
    setConfirmOpen(true);
  }

  async function confirmApply(): Promise<void> {
    if (!project || !pendingPlan) {
      return;
    }
    const plan = pendingPlan;
    setApplying(true);
    setApplyError(null);
    try {
      const summary = await client.applyMigration(project.id, plan);
      setResult(summary);
      setPendingPlan(null);
      setConfirmOpen(false);
      onChanged();
    } catch (cause) {
      setApplyError(errorText(cause));
      setPendingPlan(null);
      setConfirmOpen(false);
    } finally {
      setApplying(false);
    }
  }

  function renderOperationFields(operation: OperationDraft, index: number) {
    const id = (field: string) => `migration-op-${index}-${field}`;

    switch (operation.type) {
      case "create_table":
        return (
          <>
            <div className="field">
              <label htmlFor={id("name")}>Nombre de la tabla</label>
              <input
                id={id("name")}
                value={operation.name}
                placeholder="contactos"
                onChange={(event) => patchOperation(index, { name: event.target.value })}
              />
            </div>
            <div className="migration-columns">
              {operation.columnDrafts.map((column, columnIndex) => (
                <div className="migration-column" key={columnIndex}>
                  <div className="migration-column__head">
                    <span className="migration-column__title">Columna {columnIndex + 1}</span>
                    <button
                      type="button"
                      className="button button--ghost button--small"
                      aria-label={`Quitar columna ${columnIndex + 1}`}
                      disabled={operation.columnDrafts.length === 1}
                      onClick={() => removeColumn(index, columnIndex)}
                    >
                      Quitar
                    </button>
                  </div>
                  <div className="field">
                    <label htmlFor={id(`col-${columnIndex}-name`)}>Nombre</label>
                    <input
                      id={id(`col-${columnIndex}-name`)}
                      value={column.name}
                      placeholder="id"
                      onChange={(event) => patchColumn(index, columnIndex, { name: event.target.value })}
                    />
                  </div>
                  <div className="field">
                    <label htmlFor={id(`col-${columnIndex}-type`)}>Tipo</label>
                    <select
                      id={id(`col-${columnIndex}-type`)}
                      value={column.type}
                      onChange={(event) => patchColumn(index, columnIndex, { type: event.target.value })}
                    >
                      {["uuid", "text", "int2", "int4", "int8", "boolean", "timestamptz", "timestamp", "date", "jsonb", "json", "numeric", "varchar"].map(
                        (type) => (
                          <option key={type} value={type}>
                            {type}
                          </option>
                        )
                      )}
                    </select>
                  </div>
                  {column.type === "varchar" ? (
                    <div className="field">
                      <label htmlFor={id(`col-${columnIndex}-length`)}>Longitud</label>
                      <input
                        id={id(`col-${columnIndex}-length`)}
                        type="number"
                        min={1}
                        max={8000}
                        value={column.varcharLength}
                        onChange={(event) =>
                          patchColumn(index, columnIndex, { varcharLength: event.target.value })
                        }
                      />
                    </div>
                  ) : null}
                  <div className="field">
                    <label htmlFor={id(`col-${columnIndex}-default-mode`)}>Valor por defecto</label>
                    <select
                      id={id(`col-${columnIndex}-default-mode`)}
                      value={column.defaultMode}
                      onChange={(event) =>
                        patchColumn(index, columnIndex, {
                          defaultMode: event.target.value as ColumnDraft["defaultMode"]
                        })
                      }
                    >
                      <option value="none">Sin valor</option>
                      <option value="gen_random_uuid()">gen_random_uuid()</option>
                      <option value="now()">now()</option>
                      <option value="true">true</option>
                      <option value="false">false</option>
                      <option value="number">Número</option>
                      <option value="text">Texto</option>
                    </select>
                  </div>
                  {column.defaultMode === "number" || column.defaultMode === "text" ? (
                    <div className="field">
                      <label htmlFor={id(`col-${columnIndex}-default-value`)}>Valor</label>
                      <input
                        id={id(`col-${columnIndex}-default-value`)}
                        value={column.defaultValue}
                        onChange={(event) =>
                          patchColumn(index, columnIndex, { defaultValue: event.target.value })
                        }
                      />
                    </div>
                  ) : null}
                  <fieldset className="migration-choice">
                    <legend>Opciones</legend>
                    <div className="migration-checkboxes">
                      <label>
                        <input
                          type="checkbox"
                          checked={!column.nullable}
                          onChange={(event) => patchColumn(index, columnIndex, { nullable: !event.target.checked })}
                        />
                        Obligatoria
                      </label>
                      <label>
                        <input
                          type="checkbox"
                          checked={column.primaryKey}
                          onChange={(event) => patchColumn(index, columnIndex, { primaryKey: event.target.checked })}
                        />
                        Clave primaria
                      </label>
                      <label>
                        <input
                          type="checkbox"
                          checked={column.unique}
                          onChange={(event) => patchColumn(index, columnIndex, { unique: event.target.checked })}
                        />
                        Única
                      </label>
                    </div>
                  </fieldset>
                  <div className="field__row">
                    <div className="field">
                      <label htmlFor={id(`col-${columnIndex}-ref-table`)}>Referencia (tabla)</label>
                      <input
                        id={id(`col-${columnIndex}-ref-table`)}
                        value={column.referencesTable}
                        placeholder="proyectos"
                        onChange={(event) =>
                          patchColumn(index, columnIndex, { referencesTable: event.target.value })
                        }
                      />
                    </div>
                    <div className="field">
                      <label htmlFor={id(`col-${columnIndex}-ref-column`)}>Referencia (columna)</label>
                      <input
                        id={id(`col-${columnIndex}-ref-column`)}
                        value={column.referencesColumn}
                        placeholder="id"
                        onChange={(event) =>
                          patchColumn(index, columnIndex, { referencesColumn: event.target.value })
                        }
                      />
                    </div>
                  </div>
                </div>
              ))}
            </div>
            <button type="button" className="button button--ghost button--small" onClick={() => addColumn(index)}>
              Añadir columna
            </button>
          </>
        );

      case "drop_table":
        return (
          <div className="field">
            <label htmlFor={id("table")}>Tabla</label>
            <input
              id={id("table")}
              value={operation.table}
              placeholder="contactos"
              onChange={(event) => patchOperation(index, { table: event.target.value })}
            />
          </div>
        );

      case "drop_column":
        return (
          <div className="field__row">
            <div className="field">
              <label htmlFor={id("table")}>Tabla</label>
              <input
                id={id("table")}
                value={operation.table}
                onChange={(event) => patchOperation(index, { table: event.target.value })}
              />
            </div>
            <div className="field">
              <label htmlFor={id("column")}>Columna</label>
              <input
                id={id("column")}
                value={operation.column}
                onChange={(event) => patchOperation(index, { column: event.target.value })}
              />
            </div>
          </div>
        );

      case "create_index":
        return (
          <>
            <div className="field__row">
              <div className="field">
                <label htmlFor={id("table")}>Tabla</label>
                <input
                  id={id("table")}
                  value={operation.table}
                  onChange={(event) => patchOperation(index, { table: event.target.value })}
                />
              </div>
              <div className="field">
                <label htmlFor={id("name")}>Índice</label>
                <input
                  id={id("name")}
                  value={operation.name}
                  placeholder="contactos_email_idx"
                  onChange={(event) => patchOperation(index, { name: event.target.value })}
                />
              </div>
            </div>
            <div className="field">
              <label htmlFor={id("columns")}>Columnas (separadas por comas)</label>
              <input
                id={id("columns")}
                value={operation.columns}
                placeholder="email, estado"
                onChange={(event) => patchOperation(index, { columns: event.target.value })}
              />
            </div>
            <label className="migration-inline-check">
              <input
                type="checkbox"
                checked={operation.unique}
                onChange={(event) => patchOperation(index, { unique: event.target.checked })}
              />
              Índice único
            </label>
          </>
        );

      case "drop_index":
        return (
          <div className="field__row">
            <div className="field">
              <label htmlFor={id("table")}>Tabla</label>
              <input
                id={id("table")}
                value={operation.table}
                onChange={(event) => patchOperation(index, { table: event.target.value })}
              />
            </div>
            <div className="field">
              <label htmlFor={id("index")}>Índice</label>
              <input
                id={id("index")}
                value={operation.index}
                onChange={(event) => patchOperation(index, { index: event.target.value })}
              />
            </div>
          </div>
        );

      case "create_policy":
      case "alter_policy":
        return (
          <>
            <div className="field__row">
              <div className="field">
                <label htmlFor={id("table")}>Tabla</label>
                <input
                  id={id("table")}
                  value={operation.table}
                  onChange={(event) => patchOperation(index, { table: event.target.value })}
                />
              </div>
              {operation.type === "alter_policy" ? (
                <div className="field">
                  <label htmlFor={id("policy")}>Política actual</label>
                  <input
                    id={id("policy")}
                    value={operation.policy}
                    onChange={(event) =>
                      patchOperation(index, { policy: event.target.value, name: event.target.value })
                    }
                  />
                </div>
              ) : null}
              <div className="field">
                <label htmlFor={id("name")}>
                  {operation.type === "alter_policy" ? "Nombre de la política" : "Política"}
                </label>
                <input
                  id={id("name")}
                  value={operation.name}
                  onChange={(event) => patchOperation(index, { name: event.target.value })}
                />
              </div>
            </div>
            <fieldset className="migration-choice">
              <legend>Comandos</legend>
              <div className="migration-checkboxes">
                {POLICY_COMMANDS.map((command) => (
                  <label key={command}>
                    <input
                      type="checkbox"
                      checked={operation.commands.includes(command)}
                      onChange={() => toggleCommand(index, command)}
                    />
                    {command}
                  </label>
                ))}
              </div>
            </fieldset>
            <fieldset className="migration-choice">
              <legend>Roles</legend>
              <div className="migration-checkboxes">
                {POLICY_ROLES.map((role) => (
                  <label key={role}>
                    <input
                      type="checkbox"
                      checked={operation.roles.includes(role)}
                      onChange={() => toggleRole(index, role)}
                    />
                    {role}
                  </label>
                ))}
              </div>
            </fieldset>
            <div className="field__row">
              <div className="field">
                <label htmlFor={id("using")}>USING</label>
                <select
                  id={id("using")}
                  value={operation.using}
                  onChange={(event) => patchOperation(index, { using: event.target.value })}
                >
                  {POLICY_EXPRESSIONS.map((expression) => (
                    <option key={expression} value={expression}>
                      {expression}
                    </option>
                  ))}
                </select>
              </div>
              <div className="field">
                <label htmlFor={id("with-check")}>WITH CHECK</label>
                <select
                  id={id("with-check")}
                  value={operation.withCheck}
                  onChange={(event) => patchOperation(index, { withCheck: event.target.value })}
                >
                  {POLICY_EXPRESSIONS.map((expression) => (
                    <option key={expression} value={expression}>
                      {expression}
                    </option>
                  ))}
                </select>
              </div>
            </div>
          </>
        );

      case "drop_policy":
        return (
          <div className="field__row">
            <div className="field">
              <label htmlFor={id("table")}>Tabla</label>
              <input
                id={id("table")}
                value={operation.table}
                onChange={(event) => patchOperation(index, { table: event.target.value })}
              />
            </div>
            <div className="field">
              <label htmlFor={id("policy")}>Política</label>
              <input
                id={id("policy")}
                value={operation.policy}
                onChange={(event) => patchOperation(index, { policy: event.target.value })}
              />
            </div>
          </div>
        );
    }
  }

  const ready = project?.status === "Ready";

  return (
    <section className="leaf__content" aria-labelledby="migration-heading">
      <div className="leaf__toolbar">
        <div>
          <h2 id="migration-heading" className="leaf__title">Migraciones</h2>
          <p className="leaf__subtitle">
            {project ? `${project.slug} · historial versionado` : "Selecciona un proyecto en el cuaderno."}
          </p>
        </div>
      </div>

      {!project ? (
        <Notice title="Sin proyecto seleccionado">Abre la división Proyectos y elige uno.</Notice>
      ) : !ready ? (
        <Notice title="Proyecto no disponible">
          Las migraciones solo se pueden aplicar cuando el proyecto está en estado <code>Ready</code>.
        </Notice>
      ) : null}

      {ready ? (
        <>
          <Notice title="Planes tipados, sin SQL">
            El panel envía versiones, identificadores, tipos y plantillas de política allowlisted. El servidor
            calcula el checksum, ejecuta el DDL como propietario y audita la operación. Las operaciones
            <code> add_column </code> y <code>alter_column</code> no se ofrecen aquí porque el parser comparte
            el campo <code>type</code> con el tipo SQL; deben aplicarse desde la Control API.
          </Notice>

          {error ? <Notice tone="error" title="No se pudo leer el historial">{error}</Notice> : null}
          {applyError ? <Notice tone="error" title="La migración no se pudo aplicar">{applyError}</Notice> : null}
          {result ? (
            <Notice title={result.alreadyApplied ? "Migración ya aplicada" : "Migración aplicada"}>
              Versión {result.version} · <span className="mono">{result.name}</span> · {result.operationCount}{" "}
              {result.operationCount === 1 ? "operación" : "operaciones"} · <span className="mono">{result.checksum}</span>
            </Notice>
          ) : null}

          <div className="leaf__group">
            <h3 id="migration-history-heading" className="leaf__group-title">Historial</h3>
            {loading ? <p className="loading" role="status">Cargando historial…</p> : null}
            {!loading && migrations.length === 0 && !error ? (
              <p className="empty-inline">Este proyecto todavía no tiene migraciones registradas.</p>
            ) : null}
            {migrations.length > 0 ? (
              <div className="table-wrap" role="region" tabIndex={0} aria-labelledby="migration-history-caption">
                <table className="data-table data-table--dense" aria-labelledby="migration-history-caption">
                  <caption id="migration-history-caption" className="sr-only">
                    Migraciones aplicadas al proyecto
                  </caption>
                  <thead>
                    <tr>
                      <th scope="col">Versión</th>
                      <th scope="col">Nombre</th>
                      <th scope="col">Operaciones</th>
                      <th scope="col">Checksum</th>
                      <th scope="col">Aplicada</th>
                    </tr>
                  </thead>
                  <tbody>
                    {migrations.map((migration) => (
                      <tr key={migration.version}>
                        <td data-label="Versión" className="strong mono">{migration.version}</td>
                        <td data-label="Nombre"><span className="mono">{migration.name}</span></td>
                        <td data-label="Operaciones">{migration.operationCount}</td>
                        <td data-label="Checksum"><span className="mono">{migration.checksum}</span></td>
                        <td data-label="Aplicada">{formatDate(migration.appliedAt)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : null}
          </div>

          <div className="leaf__group">
            <h3 id="migration-build-heading" className="leaf__group-title">Nueva migración</h3>
            <div className="migration-plan">
              <div className="form form--inline">
                <div className="field">
                  <label htmlFor="migration-version">Versión</label>
                  <input
                    id="migration-version"
                    type="number"
                    min={1}
                    max={10000}
                    value={version}
                    onChange={(event) => {
                      clearFeedback();
                      setVersion(event.target.value);
                    }}
                  />
                </div>
                <div className="field field--grow">
                  <label htmlFor="migration-name">Nombre</label>
                  <input
                    id="migration-name"
                    value={name}
                    placeholder="initial_schema"
                    onChange={(event) => {
                      clearFeedback();
                      setName(event.target.value);
                    }}
                  />
                </div>
              </div>

              <div className="migration-ops">
                {drafts.map((operation, index) => (
                  <div className="migration-op" key={index}>
                    <div className="migration-op__head">
                      <div className="field field--grow">
                        <label htmlFor={`migration-op-${index}-type`}>Operación {index + 1}</label>
                        <select
                          id={`migration-op-${index}-type`}
                          value={operation.type}
                          onChange={(event) =>
                            patchOperation(index, { type: event.target.value as MigrationOperationType })
                          }
                        >
                          {MIGRATION_OPERATION_TYPES.map((type) => (
                            <option key={type} value={type}>
                              {MIGRATION_OPERATION_LABELS[type]}
                            </option>
                          ))}
                        </select>
                      </div>
                      <button
                        type="button"
                        className="button button--ghost button--small"
                        disabled={drafts.length === 1}
                        onClick={() => removeOperation(index)}
                      >
                        Quitar operación
                      </button>
                    </div>
                    {renderOperationFields(operation, index)}
                  </div>
                ))}
              </div>

              <div className="form form--inline">
                <div className="field field--grow">
                  <label htmlFor="migration-new-type">Añadir operación</label>
                  <select
                    id="migration-new-type"
                    value={newType}
                    onChange={(event) => setNewType(event.target.value as MigrationOperationType)}
                  >
                    {MIGRATION_OPERATION_TYPES.map((type) => (
                      <option key={type} value={type}>
                        {MIGRATION_OPERATION_LABELS[type]}
                      </option>
                    ))}
                  </select>
                </div>
                <button type="button" className="button button--ghost" onClick={addOperation}>
                  Añadir
                </button>
              </div>

              <div className="leaf__group">
                <h4 id="migration-preview-heading" className="leaf__group-title">Vista previa del plan</h4>
                {built.ok ? (
                  <pre className="migration-preview" aria-labelledby="migration-preview-heading">
                    {JSON.stringify(built.payload, null, 2)}
                  </pre>
                ) : (
                  <ul className="migration-validation" aria-labelledby="migration-preview-heading">
                    {built.errors.map((message) => (
                      <li key={message}>{message}</li>
                    ))}
                  </ul>
                )}
              </div>

              {validation.length > 0 ? (
                <ul className="migration-validation">
                  {validation.map((message) => (
                    <li key={message}>{message}</li>
                  ))}
                </ul>
              ) : null}

              <button
                type="button"
                className="button button--primary"
                disabled={!built.ok || applying}
                onClick={review}
              >
                Revisar y aplicar
              </button>
            </div>
          </div>
        </>
      ) : null}

      <ConfirmDialog
        open={confirmOpen}
        title="Aplicar migración"
        confirmLabel="Aplicar migración"
        busy={applying}
        onClose={() => {
          if (!applying) {
            setConfirmOpen(false);
            setPendingPlan(null);
          }
        }}
        onConfirm={() => void confirmApply()}
      >
        <p>
          Se aplicará la versión <span className="mono">{pendingPlan?.version}</span> ·{" "}
          <span className="mono">{pendingPlan?.name}</span> con {pendingPlan?.operations.length}{" "}
          {pendingPlan?.operations.length === 1 ? "operación" : "operaciones"} en la base del proyecto.
        </p>
        <p>El DDL se ejecutará en una sola transacción; cualquier error revertirá todos los cambios.</p>
      </ConfirmDialog>
    </section>
  );
}
