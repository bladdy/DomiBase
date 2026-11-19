import type { Project, SchemaFunction, SchemaTable, SchemaView } from "../lib/control-client";
import { Notice } from "./Notice";

interface SchemaSectionProps {
  readonly project: Project | null;
  readonly tables: readonly SchemaTable[];
  readonly views: readonly SchemaView[];
  readonly functions: readonly SchemaFunction[];
  readonly loadingTables: boolean;
  readonly loadingViews: boolean;
  readonly loadingFunctions: boolean;
  readonly tablesError: string | null;
  readonly viewsError: string | null;
  readonly functionsError: string | null;
}

export function SchemaSection({
  project,
  tables,
  views,
  functions,
  loadingTables,
  loadingViews,
  loadingFunctions,
  tablesError,
  viewsError,
  functionsError
}: SchemaSectionProps) {
  return (
    <section className="leaf__content" aria-labelledby="schema-heading">
      <div className="leaf__toolbar">
        <div>
          <h2 id="schema-heading" className="leaf__title">Esquema</h2>
          <p className="leaf__subtitle">
            {project
              ? `${project.slug} · esquema public · catálogo en vivo, solo lectura`
              : "Selecciona un proyecto en el cuaderno."}
          </p>
        </div>
      </div>

      {!project ? <Notice title="Sin proyecto seleccionado">Abre la división Proyectos y elige uno.</Notice> : null}
      {tablesError ? <Notice tone="error" title="No se pudieron leer las tablas">{tablesError}</Notice> : null}
      {viewsError ? <Notice tone="error" title="No se pudieron leer las vistas">{viewsError}</Notice> : null}
      {functionsError ? <Notice tone="error" title="No se pudieron leer las funciones">{functionsError}</Notice> : null}

      <div className="leaf__group">
        <h3 id="schema-tables-heading" className="leaf__group-title">Tablas</h3>
        {loadingTables ? (
          <p className="loading" role="status">Leyendo tablas…</p>
        ) : tables.length === 0 ? (
          <p className="empty-inline">No hay tablas en el esquema `public`.</p>
        ) : (
          <div className="table-wrap">
            <table className="data-table data-table--dense" aria-labelledby="schema-tables-heading">
              <caption className="sr-only">Tablas del esquema public del proyecto seleccionado</caption>
              <thead>
                <tr><th scope="col">Nombre</th></tr>
              </thead>
              <tbody>
                {tables.map((table) => (
                  <tr key={table.name}>
                    <td data-label="Nombre"><span className="mono strong">{table.name}</span></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <div className="leaf__group">
        <h3 id="schema-views-heading" className="leaf__group-title">Vistas</h3>
        {loadingViews ? (
          <p className="loading" role="status">Leyendo vistas…</p>
        ) : views.length === 0 ? (
          <p className="empty-inline">No hay vistas ni vistas materializadas en el esquema `public`.</p>
        ) : (
          <div className="table-wrap">
            <table className="data-table data-table--dense" aria-labelledby="schema-views-heading">
              <caption className="sr-only">Vistas del esquema public del proyecto seleccionado</caption>
              <thead>
                <tr><th scope="col">Nombre</th></tr>
              </thead>
              <tbody>
                {views.map((view) => (
                  <tr key={view.name}>
                    <td data-label="Nombre"><span className="mono strong">{view.name}</span></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <div className="leaf__group">
        <h3 id="schema-functions-heading" className="leaf__group-title">Funciones</h3>
        {loadingFunctions ? (
          <p className="loading" role="status">Leyendo funciones…</p>
        ) : functions.length === 0 ? (
          <p className="empty-inline">No hay funciones definidas por el usuario en el esquema `public`.</p>
        ) : (
          <div className="table-wrap">
            <table className="data-table data-table--dense" aria-labelledby="schema-functions-heading">
              <caption className="sr-only">Funciones del esquema public del proyecto seleccionado</caption>
              <thead>
                <tr>
                  <th scope="col">Nombre</th>
                  <th scope="col">Argumentos</th>
                  <th scope="col">Retorno</th>
                </tr>
              </thead>
              <tbody>
                {functions.map((fn) => (
                  <tr key={`${fn.name}(${fn.arguments})`}>
                    <td data-label="Nombre"><span className="mono strong">{fn.name}</span></td>
                    <td data-label="Argumentos">
                      {fn.arguments ? <span className="mono">{fn.arguments}</span> : <span className="muted">—</span>}
                    </td>
                    <td data-label="Retorno"><span className="mono">{fn.returnType}</span></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </section>
  );
}
