import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import {
  ControlApiError,
  type ControlClient,
  type DataPreviewResult,
  type Project,
  type RlsPolicy,
  type SchemaColumn,
  type SchemaTable
} from "../lib/control-client";
import { errorText } from "../lib/format";
import { ConfirmDialog } from "./ConfirmDialog";
import { Notice } from "./Notice";

interface TableEditorSectionProps {
  readonly client: ControlClient;
  readonly project: Project | null;
  readonly tables: readonly SchemaTable[];
  readonly loadingTables: boolean;
  readonly tablesError: string | null;
  readonly onTablesChanged: () => void;
}

interface ColumnDraft {
  readonly key: number;
  name: string;
  type: string;
  nullable: boolean;
  primaryKey: boolean;
  unique: boolean;
  identity: boolean;
  defaultText: string;
}

const sqlTypes = [
  "uuid", "text", "varchar(255)", "int4", "int8", "boolean",
  "timestamptz", "date", "jsonb", "numeric", "bytea", "inet"
] as const;

const operators = ["eq", "neq", "gt", "gte", "lt", "lte", "like", "ilike", "is"] as const;

let nextDraftKey = 1;
function blankColumn(): ColumnDraft {
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

function describeError(error: unknown): string {
  if (error instanceof ControlApiError && error.hint) {
    return `${error.message} ${error.hint}`;
  }
  return errorText(error);
}

function cellText(value: unknown): string {
  if (value === null) {
    return "NULL";
  }
  if (value === undefined) {
    return "—";
  }
  if (typeof value === "object") {
    return JSON.stringify(value);
  }
  return String(value);
}

function columnRequest(column: ColumnDraft): Record<string, unknown> {
  const request: Record<string, unknown> = {
    name: column.name.trim(),
    type: column.type,
    nullable: column.nullable,
    primaryKey: column.primaryKey,
    unique: column.unique
  };
  if (column.identity) {
    request.identity = true;
  }
  if (column.defaultText.trim() !== "") {
    request.default = column.defaultText.trim();
  }
  return request;
}

/** Editable schema and rows for one project, using only validated Control Plane operations. */
export function TableEditorSection({
  client,
  project,
  tables,
  loadingTables,
  tablesError,
  onTablesChanged
}: TableEditorSectionProps) {
  const [table, setTable] = useState("");
  const [columns, setColumns] = useState<readonly SchemaColumn[]>([]);
  const [policies, setPolicies] = useState<readonly RlsPolicy[]>([]);
  const [result, setResult] = useState<DataPreviewResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const [newTableName, setNewTableName] = useState("");
  const [newColumns, setNewColumns] = useState<readonly ColumnDraft[]>([blankColumn()]);
  const [showCreateTable, setShowCreateTable] = useState(false);
  const [columnDraft, setColumnDraft] = useState<ColumnDraft>(blankColumn());
  const [showAddColumn, setShowAddColumn] = useState(false);

  const [filterColumn, setFilterColumn] = useState("");
  const [filterOperator, setFilterOperator] = useState<string>("eq");
  const [filterValue, setFilterValue] = useState("");
  const [orderColumn, setOrderColumn] = useState("");
  const [orderDesc, setOrderDesc] = useState(false);
  const [limit, setLimit] = useState(25);
  const [offset, setOffset] = useState(0);
  const [insertText, setInsertText] = useState("{}");
  const [editFilter, setEditFilter] = useState("");
  const [editValues, setEditValues] = useState("{}");
  const [deleteFilter, setDeleteFilter] = useState("");

  const [dropTarget, setDropTarget] = useState<
    { readonly kind: "table"; readonly name: string } |
    { readonly kind: "column"; readonly table: string; readonly name: string } |
    null
  >(null);
  const [dropBusy, setDropBusy] = useState(false);
  const abortRef = useRef<AbortController | null>(null);

  useEffect(() => () => abortRef.current?.abort(), []);

  useEffect(() => {
    setTable((current) => (tables.some((item) => item.name === current) ? current : (tables[0]?.name ?? "")));
  }, [tables]);

  const loadSchema = useCallback(async () => {
    if (!project || table === "") {
      setColumns([]);
      setPolicies([]);
      return;
    }
    try {
      const [columnList, policyList] = await Promise.all([
        client.listSchemaColumns(project.id, table),
        client.listPolicies(project.id, table)
      ]);
      setColumns(columnList);
      setPolicies(policyList);
      setFilterColumn((current) => (columnList.some((item) => item.name === current) ? current : (columnList[0]?.name ?? "")));
      setOrderColumn((current) => (columnList.some((item) => item.name === current) ? current : ""));
    } catch (cause) {
      setError(describeError(cause));
    }
  }, [client, project, table]);

  useEffect(() => {
    abortRef.current?.abort();
    abortRef.current = null;
    setColumns([]);
    setPolicies([]);
    setResult(null);
    setError(null);
    setNotice(null);
    setOffset(0);
    void loadSchema();
  }, [project?.id, table, loadSchema]);

  async function run(action: () => Promise<void>, successMessage?: string): Promise<void> {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      await action();
      if (successMessage) {
        setNotice(successMessage);
      }
    } catch (cause) {
      setError(describeError(cause));
    } finally {
      setBusy(false);
    }
  }

  function readRows(): { readonly filters: readonly string[] } | null {
    const filters: string[] = [];
    if (filterColumn !== "" && filterValue.trim() !== "") {
      filters.push(`${filterColumn}.${filterOperator}.${filterValue.trim()}`);
    }
    return { filters };
  }

  async function loadRows(event?: FormEvent<HTMLFormElement>): Promise<void> {
    event?.preventDefault();
    if (!project || table === "") {
      return;
    }
    const query = readRows();
    if (!query) {
      return;
    }
    const controller = new AbortController();
    abortRef.current?.abort();
    abortRef.current = controller;
    setBusy(true);
    setError(null);
    try {
      const response = await client.previewTableRows(
        project.id,
        table,
        {
          filters: query.filters,
          ...(orderColumn === "" ? {} : { order: `${orderColumn}.${orderDesc ? "desc" : "asc"}` }),
          limit,
          offset
        },
        controller.signal
      );
      setResult(response);
    } catch (cause) {
      if (!(cause instanceof DOMException && cause.name === "AbortError")) {
        setError(describeError(cause));
      }
    } finally {
      if (abortRef.current === controller) {
        setBusy(false);
      }
    }
  }

  function parseJson(text: string): unknown {
    return JSON.parse(text) as unknown;
  }

  async function handleInsert(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault();
    if (!project || table === "") {
      return;
    }
    await run(async () => {
      const parsed = parseJson(insertText);
      const rows = Array.isArray(parsed) ? parsed : [parsed];
      const response = await client.insertRows(project.id!, table, rows as Record<string, unknown>[]);
      setNotice(`${response.affected} fila(s) insertada(s).`);
      await loadRows();
    });
  }

  async function handleUpdate(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault();
    if (!project || table === "" || editFilter.trim() === "") {
      setError("Indica un filtro acotado, por ejemplo id.eq.1.");
      return;
    }
    await run(async () => {
      const values = parseJson(editValues);
      if (values === null || typeof values !== "object" || Array.isArray(values)) {
        throw new Error("Los valores a actualizar deben ser un objeto JSON.");
      }
      const response = await client.updateRows(
        project.id!,
        table,
        values as Record<string, unknown>,
        editFilter.split(",").map((item) => item.trim()).filter((item) => item !== "")
      );
      setNotice(`${response.affected} fila(s) actualizada(s).`);
      await loadRows();
    });
  }

  async function handleDelete(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault();
    if (!project || table === "" || deleteFilter.trim() === "") {
      setError("Indica un filtro acotado, por ejemplo id.eq.1.");
      return;
    }
    await run(async () => {
      const response = await client.deleteRows(
        project.id!,
        table,
        deleteFilter.split(",").map((item) => item.trim()).filter((item) => item !== "")
      );
      setNotice(`${response.affected} fila(s) eliminada(s).`);
      await loadRows();
    });
  }

  async function handleCreateTable(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault();
    if (!project || newTableName.trim() === "" || newColumns.length === 0) {
      return;
    }
    await run(async () => {
      await client.createTable(project.id!, {
        name: newTableName.trim(),
        columns: newColumns.map((column) => columnRequest(column)) as never
      });
      setNewTableName("");
      setNewColumns([blankColumn()]);
      setShowCreateTable(false);
      onTablesChanged();
      setNotice(`Tabla ${newTableName.trim()} creada. Configura el acceso antes de usar el SDK.`);
    }, undefined);
  }

  async function handleAddColumn(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault();
    if (!project || table === "" || columnDraft.name.trim() === "") {
      return;
    }
    await run(async () => {
      await client.addColumn(project.id!, table, columnRequest(columnDraft) as never);
      setColumnDraft(blankColumn());
      setShowAddColumn(false);
      await loadSchema();
      setNotice("Columna añadida.");
    });
  }

  async function handleDrop(): Promise<void> {
    if (!project || !dropTarget) {
      return;
    }
    setDropBusy(true);
    setError(null);
    try {
      if (dropTarget.kind === "table") {
        await client.dropTable(project.id!, dropTarget.name);
        setTable("");
        onTablesChanged();
        setNotice(`Tabla ${dropTarget.name} eliminada.`);
      } else {
        await client.dropColumn(project.id!, dropTarget.table, dropTarget.name);
        await loadSchema();
        setNotice(`Columna ${dropTarget.name} eliminada.`);
      }
      setDropTarget(null);
    } catch (cause) {
      setError(describeError(cause));
    } finally {
      setDropBusy(false);
    }
  }

  async function togglePolicy(command: "SELECT" | "INSERT", enabled: boolean): Promise<void> {
    if (!project || table === "") {
      return;
    }
    const name = `anon_${command.toLowerCase()}`;
    await run(async () => {
      if (enabled) {
        await client.createPolicy(project.id!, table, {
          name,
          commands: [command],
          roles: ["anon"],
          using: "true",
          withCheck: "true"
        });
      } else {
        await client.deletePolicy(project.id!, table, name);
      }
      const refreshed = await client.listPolicies(project.id!, table);
      setPolicies(refreshed);
    }, enabled ? `Lectura anónima habilitada para ${table}.` : `Acceso anónimo retirado de ${table}.`);
  }

  function hasPolicy(command: string): boolean {
    return policies.some(
      (policy) => policy.roles.includes("anon") && policy.commands.includes(command)
    );
  }

  if (!project) {
    return (
      <section className="leaf__content">
        <h2 className="leaf__title">Table Editor</h2>
        <p className="leaf__subtitle">Selecciona un proyecto para editar sus tablas.</p>
      </section>
    );
  }

  return (
    <section className="leaf__content" aria-labelledby="tables-heading">
      <div className="leaf__toolbar">
        <div>
          <h2 id="tables-heading" className="leaf__title">Table Editor</h2>
          <p className="leaf__subtitle">
            Esquema, accesos y registros de <span className="mono">{project.slug}</span>.
          </p>
        </div>
        <button
          type="button"
          className="button button--primary"
          onClick={() => setShowCreateTable((open) => !open)}
          disabled={busy}
        >
          {showCreateTable ? "Cerrar" : "Nueva tabla"}
        </button>
      </div>

      {notice ? <Notice title="Operación completada">{notice}</Notice> : null}
      {error ? <Notice tone="error" title="No se pudo completar la operación">{error}</Notice> : null}
      {tablesError ? <Notice tone="error" title="No se pudieron cargar las tablas">{tablesError}</Notice> : null}

      {showCreateTable ? (
        <form className="form" onSubmit={(event) => void handleCreateTable(event)}>
          <div className="field">
            <label htmlFor="new-table-name">Nombre de la tabla</label>
            <input
              id="new-table-name"
              value={newTableName}
              onChange={(event) => setNewTableName(event.target.value)}
              placeholder="contactos"
              spellCheck={false}
            />
          </div>
          {newColumns.map((column, index) => (
            <div className="schema-draft" key={column.key}>
              <p className="schema-draft__title">Columna {index + 1}</p>
              <div className="schema-draft__grid">
                <div className="field">
                  <label htmlFor={`new-col-name-${column.key}`}>Nombre</label>
                  <input
                    id={`new-col-name-${column.key}`}
                    value={column.name}
                    onChange={(event) =>
                      setNewColumns((current) =>
                        current.map((item) => item.key === column.key ? { ...item, name: event.target.value } : item)
                      )
                    }
                    placeholder="email"
                    spellCheck={false}
                  />
                </div>
                <div className="field">
                  <label htmlFor={`new-col-type-${column.key}`}>Tipo</label>
                  <select
                    id={`new-col-type-${column.key}`}
                    value={column.type}
                    onChange={(event) =>
                      setNewColumns((current) =>
                        current.map((item) => item.key === column.key ? { ...item, type: event.target.value } : item)
                      )
                    }
                  >
                    {sqlTypes.map((sqlType) => <option key={sqlType} value={sqlType}>{sqlType}</option>)}
                  </select>
                </div>
                <div className="field">
                  <label htmlFor={`new-col-default-${column.key}`}>Valor por defecto</label>
                  <input
                    id={`new-col-default-${column.key}`}
                    value={column.defaultText}
                    onChange={(event) =>
                      setNewColumns((current) =>
                        current.map((item) => item.key === column.key ? { ...item, defaultText: event.target.value } : item)
                      )
                    }
                    placeholder="gen_random_uuid()"
                    spellCheck={false}
                  />
                </div>
              </div>
              <div className="schema-draft__flags">
                <label><input type="checkbox" checked={column.nullable} onChange={(event) =>
                  setNewColumns((current) => current.map((item) => item.key === column.key ? { ...item, nullable: event.target.checked } : item))
                } /> nullable</label>
                <label><input type="checkbox" checked={column.primaryKey} onChange={(event) =>
                  setNewColumns((current) => current.map((item) => item.key === column.key ? { ...item, primaryKey: event.target.checked } : item))
                } /> PK</label>
                <label><input type="checkbox" checked={column.unique} onChange={(event) =>
                  setNewColumns((current) => current.map((item) => item.key === column.key ? { ...item, unique: event.target.checked } : item))
                } /> UNIQUE</label>
                <label><input type="checkbox" checked={column.identity} onChange={(event) =>
                  setNewColumns((current) => current.map((item) => item.key === column.key ? { ...item, identity: event.target.checked } : item))
                } /> IDENTITY</label>
              </div>
              {newColumns.length > 1 ? (
                <button
                  type="button"
                  className="button button--ghost button--small"
                  onClick={() => setNewColumns((current) => current.filter((item) => item.key !== column.key))}
                >
                  Quitar columna
                </button>
              ) : null}
            </div>
          ))}
          <div className="row-actions">
            <button type="button" className="button button--ghost button--small" onClick={() => setNewColumns((current) => [...current, blankColumn()])}>
              Añadir columna
            </button>
            <button type="submit" className="button button--primary" disabled={busy}>Crear tabla</button>
          </div>
        </form>
      ) : null}

      <div className="leaf__group">
        <h3 className="leaf__group-title">Tablas</h3>
        {loadingTables ? <p className="loading" role="status">Leyendo tablas…</p> : null}
        {!loadingTables && tables.length === 0 ? <p className="empty-inline">Todavía no hay tablas.</p> : null}
        <div className="table-picker">
          {tables.map((item) => (
            <div className="table-picker__item" key={item.name}>
              <button
                type="button"
                className={`button button--small${item.name === table ? " button--primary" : " button--ghost"}`}
                onClick={() => setTable(item.name)}
              >
                {item.name}
              </button>
              <button
                type="button"
                className="button button--danger button--small"
                aria-label={`Eliminar tabla ${item.name}`}
                onClick={() => setDropTarget({ kind: "table", name: item.name })}
              >
                ×
              </button>
            </div>
          ))}
        </div>
      </div>

      {table !== "" ? (
        <>
          <div className="leaf__group">
            <div className="leaf__toolbar">
              <h3 className="leaf__group-title">Columnas de {table}</h3>
              <button type="button" className="button button--small" onClick={() => setShowAddColumn((open) => !open)}>
                {showAddColumn ? "Cerrar" : "Añadir columna"}
              </button>
            </div>
            {showAddColumn ? (
              <form className="form form--inline" onSubmit={(event) => void handleAddColumn(event)}>
                <div className="field">
                  <label htmlFor="add-col-name">Nombre</label>
                  <input id="add-col-name" value={columnDraft.name} onChange={(event) => setColumnDraft({ ...columnDraft, name: event.target.value })} spellCheck={false} />
                </div>
                <div className="field">
                  <label htmlFor="add-col-type">Tipo</label>
                  <select id="add-col-type" value={columnDraft.type} onChange={(event) => setColumnDraft({ ...columnDraft, type: event.target.value })}>
                    {sqlTypes.map((sqlType) => <option key={sqlType} value={sqlType}>{sqlType}</option>)}
                  </select>
                </div>
                <div className="field">
                  <label htmlFor="add-col-default">Default</label>
                  <input id="add-col-default" value={columnDraft.defaultText} onChange={(event) => setColumnDraft({ ...columnDraft, defaultText: event.target.value })} spellCheck={false} />
                </div>
                <button type="submit" className="button button--primary" disabled={busy}>Añadir</button>
              </form>
            ) : null}
            <div className="table-wrap">
              <table className="data-table data-table--dense">
                <caption className="sr-only">Columnas de {table}</caption>
                <thead>
                  <tr>
                    <th scope="col">Columna</th><th scope="col">Tipo</th><th scope="col">Nulo</th>
                    <th scope="col">Por defecto</th><th scope="col">Claves</th><th scope="col">Acción</th>
                  </tr>
                </thead>
                <tbody>
                  {columns.map((column) => (
                    <tr key={column.name}>
                      <td data-label="Columna" className="mono strong">{column.name}</td>
                      <td data-label="Tipo" className="mono">{column.type}</td>
                      <td data-label="Nulo">{column.nullable ? "sí" : "no"}</td>
                      <td data-label="Por defecto" className="mono">{column.default ?? "—"}</td>
                      <td data-label="Claves">
                        {column.primaryKey ? "PK " : ""}{column.identity ? "IDENTITY " : ""}
                        {column.unique ? "UNIQUE " : ""}
                        {column.foreignTable ? `→ ${column.foreignTable}.${column.foreignColumn ?? ""}` : ""}
                      </td>
                      <td data-label="Acción">
                        <button
                          type="button"
                          className="button button--danger button--small"
                          onClick={() => setDropTarget({ kind: "column", table, name: column.name })}
                        >
                          Eliminar
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>

          <div className="leaf__group">
            <h3 className="leaf__group-title">Acceso anónimo (RLS)</h3>
            <p className="leaf__subtitle">
              Sin política, el SDK no puede leer ni escribir esta tabla. Activa solo lo necesario.
            </p>
            <div className="row-actions">
              <label className="access-toggle">
                <input
                  type="checkbox"
                  checked={hasPolicy("SELECT")}
                  onChange={(event) => void togglePolicy("SELECT", event.target.checked)}
                  disabled={busy}
                />
                Permitir SELECT
              </label>
              <label className="access-toggle">
                <input
                  type="checkbox"
                  checked={hasPolicy("INSERT")}
                  onChange={(event) => void togglePolicy("INSERT", event.target.checked)}
                  disabled={busy}
                />
                Permitir INSERT
              </label>
            </div>
          </div>

          <div className="leaf__group">
            <h3 className="leaf__group-title">Registros</h3>
            <form className="form form--inline" onSubmit={(event) => void loadRows(event)}>
              <div className="field">
                <label htmlFor="row-filter-column">Columna</label>
                <select id="row-filter-column" value={filterColumn} onChange={(event) => setFilterColumn(event.target.value)}>
                  <option value="">Sin filtro</option>
                  {columns.map((column) => <option key={column.name} value={column.name}>{column.name}</option>)}
                </select>
              </div>
              <div className="field">
                <label htmlFor="row-filter-op">Operador</label>
                <select id="row-filter-op" value={filterOperator} onChange={(event) => setFilterOperator(event.target.value)}>
                  {operators.map((operator) => <option key={operator} value={operator}>{operator}</option>)}
                </select>
              </div>
              <div className="field field--grow">
                <label htmlFor="row-filter-value">Valor</label>
                <input id="row-filter-value" value={filterValue} onChange={(event) => setFilterValue(event.target.value)} spellCheck={false} />
              </div>
              <div className="field">
                <label htmlFor="row-order-column">Orden</label>
                <select id="row-order-column" value={orderColumn} onChange={(event) => setOrderColumn(event.target.value)}>
                  <option value="">Sin orden</option>
                  {columns.map((column) => <option key={column.name} value={column.name}>{column.name}</option>)}
                </select>
              </div>
              <div className="field">
                <label htmlFor="row-order-dir">Dirección</label>
                <select id="row-order-dir" value={orderDesc ? "desc" : "asc"} onChange={(event) => setOrderDesc(event.target.value === "desc")}>
                  <option value="asc">Ascendente</option>
                  <option value="desc">Descendente</option>
                </select>
              </div>
              <div className="field">
                <label htmlFor="row-limit">Límite</label>
                <input id="row-limit" type="number" min={1} max={200} value={limit} onChange={(event) => setLimit(Number(event.target.value))} />
              </div>
              <button type="submit" className="button button--primary" disabled={busy}>Buscar</button>
            </form>
            <div className="row-actions">
              <button type="button" className="button button--small" disabled={offset === 0 || busy} onClick={() => { setOffset(Math.max(0, offset - limit)); void loadRows(); }}>
                Anterior
              </button>
              <span className="muted">Desde {offset}</span>
              <button
                type="button"
                className="button button--small"
                disabled={!result || result.rows.length < limit || busy}
                onClick={() => { setOffset(offset + limit); void loadRows(); }}
              >
                Siguiente
              </button>
            </div>
            {result ? (
              <>
                <p className="sql-meta">
                  Rol {result.role} · {result.rowCount} fila(s){result.truncated ? " · resultado truncado" : ""}
                </p>
                <div className="table-wrap">
                  <table className="data-table">
                    <caption className="sr-only">Filas de {table}</caption>
                    <thead>
                      <tr>{result.columns.map((name) => <th key={name} scope="col">{name}</th>)}</tr>
                    </thead>
                    <tbody>
                      {result.rows.map((row, index) => (
                        <tr key={index}>
                          {result.columns.map((name) => <td key={name} data-label={name}>{cellText(row[name])}</td>)}
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </>
            ) : null}
          </div>

          <div className="leaf__group">
            <h3 className="leaf__group-title">Cambiar registros</h3>
            <form className="form" onSubmit={(event) => void handleInsert(event)}>
              <div className="field field--grow">
                <label htmlFor="insert-rows">Insertar fila(s) como JSON</label>
                <textarea id="insert-rows" rows={3} value={insertText} onChange={(event) => setInsertText(event.target.value)} spellCheck={false} />
              </div>
              <button type="submit" className="button button--primary button--small" disabled={busy}>Insertar</button>
            </form>
            <form className="form" onSubmit={(event) => void handleUpdate(event)}>
              <div className="field field--grow">
                <label htmlFor="update-filter">Filtro de actualización</label>
                <input id="update-filter" value={editFilter} onChange={(event) => setEditFilter(event.target.value)} placeholder="id.eq.1" spellCheck={false} />
              </div>
              <div className="field field--grow">
                <label htmlFor="update-values">Valores JSON</label>
                <textarea id="update-values" rows={2} value={editValues} onChange={(event) => setEditValues(event.target.value)} spellCheck={false} />
              </div>
              <button type="submit" className="button button--small" disabled={busy}>Actualizar</button>
            </form>
            <form className="form form--inline" onSubmit={(event) => void handleDelete(event)}>
              <div className="field field--grow">
                <label htmlFor="delete-filter">Filtro de eliminación</label>
                <input id="delete-filter" value={deleteFilter} onChange={(event) => setDeleteFilter(event.target.value)} placeholder="id.eq.1" spellCheck={false} />
              </div>
              <button type="submit" className="button button--danger button--small" disabled={busy}>Eliminar filas</button>
            </form>
          </div>
        </>
      ) : null}

      <ConfirmDialog
        open={dropTarget !== null}
        title={dropTarget?.kind === "table" ? "Eliminar tabla" : "Eliminar columna"}
        confirmLabel="Eliminar"
        busy={dropBusy}
        onClose={() => setDropTarget(null)}
        onConfirm={() => void handleDrop()}
      >
        <p>
          Esta acción elimina {dropTarget?.kind === "table" ? "la tabla y todos sus registros" : "la columna y sus datos"}
          {" "}de forma permanente. No se puede deshacer.
        </p>
      </ConfirmDialog>
    </section>
  );
}
