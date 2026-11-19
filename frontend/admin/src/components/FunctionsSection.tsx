import type { FunctionDefinition, Project } from "../lib/control-client";
import { formatDate, shortId } from "../lib/format";
import { Notice } from "./Notice";

interface FunctionsSectionProps {
  readonly project: Project | null;
  readonly functions: readonly FunctionDefinition[];
  readonly loading: boolean;
  readonly error: string | null;
}

export function FunctionsSection({ project, functions, loading, error }: FunctionsSectionProps) {
  return (
    <section className="leaf__content" aria-labelledby="functions-heading">
      <div className="leaf__toolbar">
        <div>
          <h2 id="functions-heading" className="leaf__title">Funciones</h2>
          <p className="leaf__subtitle">
            {project ? `${project.slug} · solo lectura` : "Selecciona un proyecto en el cuaderno."}
          </p>
        </div>
      </div>

      {!project ? <Notice title="Sin proyecto seleccionado">Abre la división Proyectos y elige uno.</Notice> : null}
      {error ? <Notice tone="error" title="No se pudieron cargar las funciones">{error}</Notice> : null}

      {loading ? (
        <p className="loading" role="status">Leyendo funciones…</p>
      ) : functions.length === 0 ? (
        <div className="empty">
          <p className="empty__title">Sin funciones registradas.</p>
          <p>Las definiciones WASM aparecerán aquí con sus límites de ejecución.</p>
        </div>
      ) : (
        <div className="table-wrap">
          <table className="data-table data-table--dense">
            <caption className="sr-only">Funciones del proyecto seleccionado</caption>
            <thead>
              <tr>
                <th scope="col">Nombre</th>
                <th scope="col">Descripción</th>
                <th scope="col">Hash del módulo</th>
                <th scope="col">Límites</th>
                <th scope="col">Creada</th>
              </tr>
            </thead>
            <tbody>
              {functions.map((fn) => (
                <tr key={fn.id}>
                  <td data-label="Nombre">
                    <span className="mono strong">{fn.name}</span>
                    <span className={`enabled ${fn.enabled ? "enabled--on" : ""}`}>
                      {fn.enabled ? "Activa" : "Desactivada"}
                    </span>
                  </td>
                  <td data-label="Descripción">{fn.description ?? <span className="muted">—</span>}</td>
                  <td data-label="Hash"><span className="mono" title={fn.moduleSha256}>{shortId(fn.moduleSha256)}</span></td>
                  <td data-label="Límites">
                    <span className="limits mono">
                      fuel {fn.fuel} · {fn.memoryPages} págs · {fn.timeoutMs} ms
                    </span>
                  </td>
                  <td data-label="Creada" className="mono">{formatDate(fn.createdAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
