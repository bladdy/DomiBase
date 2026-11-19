import { useEffect, useRef, useState, type FormEvent } from "react";
import {
  ControlApiError,
  type ControlClient,
  type DataPreviewRequest,
  type DataPreviewResult,
  type Project,
  type SchemaTable
} from "../lib/control-client";
import { errorText } from "../lib/format";
import { Notice } from "./Notice";

interface DataSectionProps {
  readonly client: ControlClient;
  readonly project: Project | null;
  readonly tables: readonly SchemaTable[];
  readonly loadingTables: boolean;
  readonly tablesError: string | null;
}

type FilterField = "column" | "operator" | "value";

interface PreviewFilter {
  readonly id: number;
  column: string;
  operator: string;
  value: string;
}

const operators = [
  ["eq", "igual a"],
  ["neq", "distinto de"],
  ["gt", "mayor que"],
  ["gte", "mayor o igual que"],
  ["lt", "menor que"],
  ["lte", "menor o igual que"],
  ["like", " LIKE"],
  ["ilike", " ILIKE"],
  ["in", "en lista (a,b,c)"],
  ["is", "es nulo / verdadero / falso"]
] as const;

let nextFilterId = 1;
function blankFilter(): PreviewFilter {
  return { id: nextFilterId++, column: "", operator: "eq", value: "" };
}

function cellText(value: unknown): string {
  if (value === null) {
    return "NULL";
  }
  if (value === undefined) {
    return "—";
  }
  if (typeof value === "string") {
    return value;
  }
  if (typeof value === "object") {
    return JSON.stringify(value);
  }
  return String(value);
}

/**
 * Row browser for the private panel. It deliberately uses the administrative preview route,
 * which reuses the Runtime execution path, so what is shown here is exactly what `anon` can
 * read. The owner-level SQL editor remains a separate capability.
 */
export function DataSection({
  client,
  project,
  tables,
  loadingTables,
  tablesError
}: DataSectionProps) {
  const [table, setTable] = useState("");
  const [select, setSelect] = useState("");
  const [filters, setFilters] = useState<PreviewFilter[]>([]);
  const [orderColumn, setOrderColumn] = useState("");
  const [orderDirection, setOrderDirection] = useState<"asc" | "desc">("asc");
  const [limit, setLimit] = useState(50);
  const [offset, setOffset] = useState(0);
  const [result, setResult] = useState<DataPreviewResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const abortRef = useRef<AbortController | null>(null);

  useEffect(() => () => abortRef.current?.abort(), []);

  useEffect(() => {
    setTable((current) =>
      tables.some((item) => item.name === current) ? current : (tables[0]?.name ?? "")
    );
  }, [tables]);

  useEffect(() => {
    abortRef.current?.abort();
    abortRef.current = null;
    setResult(null);
    setError(null);
    setBusy(false);
  }, [project?.id, table]);

  function updateFilter(id: number, field: FilterField, value: string) {
    setFilters((current) =>
      current.map((filter) => (filter.id === id ? { ...filter, [field]: value } : filter))
    );
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!project || table === "") {
      return;
    }

    const incomplete = filters.find(
      (filter) => filter.column.trim() === "" || filter.value.trim() === ""
    );
    if (incomplete) {
      setError("Completa la columna y el valor de todos los filtros antes de consultar.");
      return;
    }

    const controller = new AbortController();
    abortRef.current = controller;
    setBusy(true);
    setError(null);
    setResult(null);

    try {
      const trimmedSelect = select.trim();
      const trimmedOrder = orderColumn.trim();
      const request: DataPreviewRequest = {
        ...(trimmedSelect === "" ? {} : { select: trimmedSelect }),
        filters: filters.map(
          (filter) => `${filter.column.trim()}.${filter.operator}.${filter.value.trim()}`
        ),
        ...(trimmedOrder === "" ? {} : { order: `${trimmedOrder}.${orderDirection}` }),
        limit,
        offset
      };

      const response = await client.previewTableRows(
        project.id,
        table,
        request,
        controller.signal
      );
      setResult(response);
    } catch (cause) {
      if (cause instanceof DOMException && cause.name === "AbortError") {
        return;
      }
      if (cause instanceof ControlApiError && cause.hint) {
        setError(`${cause.message} ${cause.hint}`);
      } else {
        setError(errorText(cause));
      }
    } finally {
      if (abortRef.current === controller) {
        abortRef.current = null;
        setBusy(false);
      }
    }
  }

  const ready = project?.status === "Ready";

  return (
    <section className="leaf__content" aria-labelledby="data-heading">
      <div className="leaf__toolbar">
        <div>
          <h2 id="data-heading" className="leaf__title">Datos</h2>
          <p className="leaf__subtitle">
            {project
              ? `${project.slug} · vista previa con permisos públicos reales`
              : "Selecciona un proyecto activo."}
          </p>
        </div>
      </div>

      {!project ? (
        <Notice title="Sin proyecto seleccionado">Abre la división Proyectos y elige uno.</Notice>
      ) : !ready ? (
        <Notice title="Proyecto no disponible">
          La vista previa solo funciona cuando el proyecto está en estado <code>Ready</code>.
        </Notice>
      ) : null}

      {ready ? (
        <>
          <Notice title="Lectura como anon">
            Esta vista usa el mismo rol funcional, RLS y permisos de columna que una petición pública.
            Nunca se ejecuta como propietario. Para inspeccionar sin RLS usa la división SQL.
          </Notice>

          {tablesError ? <Notice tone="error" title="No se pudo leer el esquema">{tablesError}</Notice> : null}

          <form className="form data-form" onSubmit={handleSubmit} aria-busy={busy} noValidate>
            <div className="field">
              <label htmlFor="data-table">Tabla expuesta</label>
              <select
                id="data-table"
                value={table}
                onChange={(event) => setTable(event.target.value)}
                disabled={loadingTables || tables.length === 0}
              >
                {tables.length === 0 ? (
                  <option value="">{loadingTables ? "Cargando…" : "Sin tablas"}</option>
                ) : (
                  tables.map((item) => (
                    <option key={item.name} value={item.name}>
                      {item.name}
                    </option>
                  ))
                )}
              </select>
            </div>

            <div className="field field--grow">
              <label htmlFor="data-select">Columnas (opcional)</label>
              <input
                id="data-select"
                className="mono"
                value={select}
                onChange={(event) => setSelect(event.target.value)}
                placeholder="id,nombre,email"
              />
            </div>

            <div className="field">
              <label htmlFor="data-limit">Límite</label>
              <input
                id="data-limit"
                type="number"
                min={1}
                max={200}
                value={limit}
                onChange={(event) => setLimit(Number(event.target.value))}
              />
            </div>

            <div className="field">
              <label htmlFor="data-offset">Desplazamiento</label>
              <input
                id="data-offset"
                type="number"
                min={0}
                max={100000}
                value={offset}
                onChange={(event) => setOffset(Number(event.target.value))}
              />
            </div>

            <div className="field">
              <label htmlFor="data-order-column">Ordenar por</label>
              <input
                id="data-order-column"
                className="mono"
                value={orderColumn}
                onChange={(event) => setOrderColumn(event.target.value)}
                placeholder="created_at"
              />
            </div>

            <div className="field">
              <label htmlFor="data-order-direction">Dirección</label>
              <select
                id="data-order-direction"
                value={orderDirection}
                onChange={(event) => setOrderDirection(event.target.value as "asc" | "desc")}
              >
                <option value="asc">Ascendente</option>
                <option value="desc">Descendente</option>
              </select>
            </div>

            <fieldset className="filter-set">
              <legend>Filtros (máximo 10)</legend>
              {filters.length === 0 ? (
                <p className="hint">Sin filtros: se devolverán las filas visibles para anon.</p>
              ) : null}
              {filters.map((filter) => (
                <div className="filter-row" key={filter.id}>
                  <label className="sr-only" htmlFor={`filter-column-${filter.id}`}>
                    Columna del filtro
                  </label>
                  <input
                    id={`filter-column-${filter.id}`}
                    className="mono"
                    value={filter.column}
                    onChange={(event) => updateFilter(filter.id, "column", event.target.value)}
                    placeholder="estado"
                  />
                  <label className="sr-only" htmlFor={`filter-operator-${filter.id}`}>
                    Operador del filtro
                  </label>
                  <select
                    id={`filter-operator-${filter.id}`}
                    value={filter.operator}
                    onChange={(event) => updateFilter(filter.id, "operator", event.target.value)}
                  >
                    {operators.map(([value, label]) => (
                      <option key={value} value={value}>
                        {label}
                      </option>
                    ))}
                  </select>
                  <label className="sr-only" htmlFor={`filter-value-${filter.id}`}>
                    Valor del filtro
                  </label>
                  <input
                    id={`filter-value-${filter.id}`}
                    className="mono"
                    value={filter.value}
                    onChange={(event) => updateFilter(filter.id, "value", event.target.value)}
                    placeholder="nuevo"
                  />
                  <button
                    type="button"
                    className="button button--ghost button--small"
                    onClick={() => setFilters((current) => current.filter((item) => item.id !== filter.id))}
                  >
                    Quitar
                  </button>
                </div>
              ))}
              <button
                type="button"
                className="button button--ghost button--small"
                onClick={() => setFilters((current) => [...current, blankFilter()])}
                disabled={filters.length >= 10}
              >
                Añadir filtro
              </button>
            </fieldset>

            <button
              type="submit"
              className="button button--primary"
              disabled={busy || table === ""}
            >
              {busy ? "Leyendo…" : "Leer filas"}
            </button>
          </form>

          {error ? <Notice tone="error" title="La vista previa no se pudo completar">{error}</Notice> : null}

          {result ? (
            <div className="leaf__group" aria-live="polite">
              <h3 id="data-results-heading" className="leaf__group-title">Filas visibles</h3>
              <p className="sql-meta" role="status">
                <span className="access-chip mono">{result.role}</span>{" "}
                {result.rowCount} {result.rowCount === 1 ? "fila" : "filas"} · tabla{" "}
                <code>{table}</code>
                {result.truncated ? " · hay más filas" : ""}
              </p>
              {result.rows.length === 0 ? (
                <p className="empty-inline">
                  No hay filas visibles para anon. Puede no haber datos o no existir una política SELECT.
                </p>
              ) : (
                <div className="table-wrap preview-table-wrap">
                  <table className="data-table data-table--dense" aria-labelledby="data-results-heading">
                    <caption className="sr-only">
                      Filas de {table} visibles para el rol anon
                    </caption>
                    <thead>
                      <tr>
                        {result.columns.map((column, index) => (
                          <th key={`${column}-${index}`} scope="col">
                            {column}
                          </th>
                        ))}
                      </tr>
                    </thead>
                    <tbody>
                      {result.rows.map((row, rowIndex) => (
                        <tr key={rowIndex}>
                          {result.columns.map((column, columnIndex) => (
                            <td key={`${column}-${columnIndex}`} data-label={column}>
                              <span className="mono">{cellText(row[column])}</span>
                            </td>
                          ))}
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          ) : null}
        </>
      ) : null}
    </section>
  );
}
