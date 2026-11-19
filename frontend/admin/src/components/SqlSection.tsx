import { useEffect, useRef, useState, type FormEvent } from "react";
import {
  ControlApiError,
  type ControlClient,
  type Project,
  type SqlQueryResult
} from "../lib/control-client";
import { errorText } from "../lib/format";
import { Notice } from "./Notice";

interface SqlSectionProps {
  readonly client: ControlClient;
  readonly project: Project | null;
}

const starterSql = "SELECT current_database() AS base, current_user AS rol";

function cellText(value: unknown): string {
  if (value === null || value === undefined) {
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
 * Administrative query console. Only read-only statements reach the Control API; the server
 * enforces the transaction mode, row cap, timeout and audit trail. The SQL itself is never
 * logged or stored by the server.
 */
export function SqlSection({ client, project }: SqlSectionProps) {
  const [sql, setSql] = useState(starterSql);
  const [maxRows, setMaxRows] = useState(100);
  const [timeoutMs, setTimeoutMs] = useState(10_000);
  const [result, setResult] = useState<SqlQueryResult | null>(null);
  const [queryError, setQueryError] = useState<string | null>(null);
  const [running, setRunning] = useState(false);
  const abortRef = useRef<AbortController | null>(null);

  useEffect(() => () => abortRef.current?.abort(), []);

  useEffect(() => {
    abortRef.current?.abort();
    abortRef.current = null;
    setResult(null);
    setQueryError(null);
    setRunning(false);
  }, [project?.id]);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!project || sql.trim().length === 0) {
      return;
    }

    const controller = new AbortController();
    abortRef.current = controller;
    setRunning(true);
    setQueryError(null);
    setResult(null);

    try {
      const response = await client.runSqlQuery(
        project.id,
        { sql: sql.trim(), maxRows, timeoutMs },
        controller.signal
      );
      setResult(response);
    } catch (cause) {
      if (cause instanceof DOMException && cause.name === "AbortError") {
        return;
      }
      if (cause instanceof ControlApiError && cause.hint) {
        setQueryError(`${cause.message} ${cause.hint}`);
      } else {
        setQueryError(errorText(cause));
      }
    } finally {
      if (abortRef.current === controller) {
        abortRef.current = null;
        setRunning(false);
      }
    }
  }

  const ready = project?.status === "Ready";

  return (
    <section className="leaf__content" aria-labelledby="sql-heading">
      <div className="leaf__toolbar">
        <div>
          <h2 id="sql-heading" className="leaf__title">SQL</h2>
          <p className="leaf__subtitle">
            {project
              ? `${project.slug} · consultas acotadas de solo lectura`
              : "Selecciona un proyecto en el cuaderno."}
          </p>
        </div>
      </div>

      {!project ? (
        <Notice title="Sin proyecto seleccionado">Abre la división Proyectos y elige uno.</Notice>
      ) : !ready ? (
        <Notice title="Proyecto no disponible">
          Las consultas solo se permiten cuando el proyecto está en estado <code>Ready</code>.
        </Notice>
      ) : null}

      {ready ? (
        <>
          <Notice title="Solo lectura">
            El servidor acepta exclusivamente una sentencia <code>SELECT</code> o <code>WITH</code>, la ejecuta
            en una transacción de solo lectura como propietario del proyecto y aplica límite de filas y timeout.
            La sentencia no se guarda ni se escribe en la auditoría.
          </Notice>

          <form className="form sql-form" onSubmit={handleSubmit} noValidate>
            <div className="field field--sql">
              <label htmlFor="sql-statement">Sentencia SQL</label>
              <textarea
                id="sql-statement"
                className="sql-editor"
                value={sql}
                onChange={(event) => setSql(event.target.value)}
                spellCheck={false}
                autoCapitalize="off"
                autoCorrect="off"
                aria-describedby="sql-help"
              />
              <p id="sql-help" className="sql-help">
                Una sola sentencia; se descartan comentarios y literales para la validación. Máximo 8.000 caracteres.
              </p>
            </div>
            <div className="field">
              <label htmlFor="sql-max-rows">Máximo de filas</label>
              <input
                id="sql-max-rows"
                type="number"
                min={1}
                max={1000}
                value={maxRows}
                onChange={(event) => setMaxRows(Number(event.target.value))}
              />
            </div>
            <div className="field">
              <label htmlFor="sql-timeout">Timeout (ms)</label>
              <input
                id="sql-timeout"
                type="number"
                min={100}
                max={30000}
                step={100}
                value={timeoutMs}
                onChange={(event) => setTimeoutMs(Number(event.target.value))}
              />
            </div>
            <button type="submit" className="button button--primary" disabled={running || sql.trim().length === 0}>
              {running ? "Ejecutando…" : "Ejecutar consulta"}
            </button>
          </form>

          {queryError ? <Notice tone="error" title="La consulta no se pudo ejecutar">{queryError}</Notice> : null}

          {result ? (
            <div className="leaf__group" aria-live="polite">
              <h3 id="sql-results-heading" className="leaf__group-title">Resultados</h3>
              <p className="sql-meta" role="status">
                {result.rowCount} {result.rowCount === 1 ? "fila" : "filas"} · {result.durationMs} ms
                {result.truncated ? ` · truncado a ${result.rowCount}` : ""}
              </p>
              {result.rows.length === 0 ? (
                <p className="empty-inline">La consulta no devolvió filas.</p>
              ) : (
                <div className="table-wrap">
                  <table className="data-table data-table--dense" aria-labelledby="sql-results-heading">
                    <caption className="sr-only">Filas devueltas por la consulta de solo lectura</caption>
                    <thead>
                      <tr>
                        {result.columns.map((column, index) => (
                          <th key={`${column}-${index}`} scope="col">{column}</th>
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
