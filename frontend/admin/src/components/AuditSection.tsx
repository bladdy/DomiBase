import { useEffect, useRef, useState, type FormEvent } from "react";
import type {
  AuditEntry,
  AuditPage,
  AuditQuery,
  ControlClient,
  ProvisioningOperation,
  Project
} from "../lib/control-client";
import { errorText, formatDate, shortId } from "../lib/format";
import { Notice } from "./Notice";

interface AuditSectionProps {
  readonly client: ControlClient;
  readonly project: Project | null;
}

interface FilterDraft {
  readonly action: string;
  readonly actor: string;
  readonly targetType: string;
  readonly from: string;
  readonly to: string;
}

const emptyFilters: FilterDraft = {
  action: "",
  actor: "",
  targetType: "",
  from: "",
  to: ""
};

type PageQuery = AuditQuery & {
  readonly limit: number;
  readonly offset: number;
};

const initialQuery: PageQuery = { limit: 50, offset: 0 };
const pageSizes = [20, 50, 100] as const;

function toQuery(draft: FilterDraft): AuditQuery {
  const action = draft.action.trim();
  const actor = draft.actor.trim();
  const targetType = draft.targetType.trim();
  return {
    ...(action ? { action } : {}),
    ...(actor ? { actor } : {}),
    ...(targetType ? { targetType } : {}),
    ...(draft.from ? { from: `${draft.from}T00:00:00.000Z` } : {}),
    ...(draft.to ? { to: `${draft.to}T23:59:59.999Z` } : {})
  };
}

/**
 * Read-only Control Plane view of administrative audit rows and the active project's
 * provisioning steps. Filters are sent as allowlisted query parameters; the browser never
 * sends SQL and never sees a secret or a filesystem path.
 */
export function AuditSection({ client, project }: AuditSectionProps) {
  const [draft, setDraft] = useState<FilterDraft>(emptyFilters);
  const [query, setQuery] = useState<PageQuery>(initialQuery);
  const [page, setPage] = useState<AuditPage | null>(null);
  const [operations, setOperations] = useState<ProvisioningOperation[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [operationsError, setOperationsError] = useState<string | null>(null);
  const generation = useRef(0);
  const operationsGeneration = useRef(0);
  const projectId = project?.id ?? null;

  useEffect(() => {
    const controller = new AbortController();
    const current = ++generation.current;
    setLoading(true);
    client
      .listAudit(query, controller.signal)
      .then((result) => {
        if (current === generation.current) {
          setPage(result);
          setError(null);
        }
      })
      .catch((cause: unknown) => {
        if (controller.signal.aborted || current !== generation.current) {
          return;
        }
        setError(errorText(cause));
      })
      .finally(() => {
        if (current === generation.current) {
          setLoading(false);
        }
      });
    return () => controller.abort();
  }, [client, query]);

  useEffect(() => {
    const controller = new AbortController();
    const current = ++operationsGeneration.current;
    if (!projectId) {
      setOperations([]);
      setOperationsError(null);
      return () => controller.abort();
    }

    client
      .listProvisioningOperations(projectId, controller.signal)
      .then((result) => {
        if (current === operationsGeneration.current) {
          setOperations(result);
          setOperationsError(null);
        }
      })
      .catch((cause: unknown) => {
        if (controller.signal.aborted || current !== operationsGeneration.current) {
          return;
        }
        setOperations([]);
        setOperationsError(errorText(cause));
      });
    return () => controller.abort();
  }, [client, projectId]);

  const apply = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setQuery((current) => ({ ...toQuery(draft), limit: current.limit, offset: 0 }));
  };

  const clear = () => {
    setDraft(emptyFilters);
    setQuery(initialQuery);
  };

  const changePageSize = (size: number) => {
    setQuery((current) => ({ ...current, limit: size, offset: 0 }));
  };

  const total = page?.total ?? 0;
  const offset = page?.offset ?? 0;
  const limit = page?.limit ?? initialQuery.limit;
  const first = total === 0 ? 0 : offset + 1;
  const last = Math.min(offset + limit, total);

  return (
    <>
      <div className="leaf__group">
        <h2 id="audit-filter-heading" className="leaf__group-title">Auditoría administrativa</h2>
        <search>
          <form
            className="form form--inline audit-filters"
            method="get"
            action="#audit-results"
            aria-labelledby="audit-filter-heading"
            onSubmit={apply}
          >
            <fieldset>
              <legend>Filtros</legend>
              <div className="field">
                <label htmlFor="audit-action">Acción</label>
                <input
                  id="audit-action"
                  name="action"
                  value={draft.action}
                  placeholder="project.create"
                  onChange={(event) => setDraft({ ...draft, action: event.target.value })}
                />
              </div>
              <div className="field">
                <label htmlFor="audit-actor">Actor</label>
                <input
                  id="audit-actor"
                  name="actor"
                  value={draft.actor}
                  placeholder="admin"
                  onChange={(event) => setDraft({ ...draft, actor: event.target.value })}
                />
              </div>
              <div className="field">
                <label htmlFor="audit-target">Tipo de objetivo</label>
                <input
                  id="audit-target"
                  name="targetType"
                  value={draft.targetType}
                  placeholder="project"
                  onChange={(event) => setDraft({ ...draft, targetType: event.target.value })}
                />
              </div>
              <div className="field">
                <label htmlFor="audit-from">Desde</label>
                <input
                  id="audit-from"
                  name="from"
                  type="date"
                  value={draft.from}
                  onChange={(event) => setDraft({ ...draft, from: event.target.value })}
                />
              </div>
              <div className="field">
                <label htmlFor="audit-to">Hasta</label>
                <input
                  id="audit-to"
                  name="to"
                  type="date"
                  value={draft.to}
                  onChange={(event) => setDraft({ ...draft, to: event.target.value })}
                />
              </div>
              <div className="field field--actions">
                <button type="submit" className="button button--primary button--small">Aplicar filtros</button>
                <button type="button" className="button button--ghost button--small" onClick={clear}>Limpiar</button>
              </div>
            </fieldset>
          </form>
        </search>

        {error ? <Notice tone="error" title="No se pudo leer la auditoría">{error}</Notice> : null}

        <p id="audit-results" className="audit-status" role="status" aria-live="polite">
          {loading ? "Cargando entradas…" : `${first}–${last} de ${total} entradas`}
        </p>

        {!loading && page?.items.length === 0 ? (
          <p className="empty-inline">Todavía no hay entradas para estos filtros.</p>
        ) : (
          <div
            className="table-wrap audit-table-wrap"
            tabIndex={0}
            role="region"
            aria-labelledby="audit-caption"
            aria-busy={loading}
          >
            <table className="data-table audit-table">
              <caption id="audit-caption" className="sr-only">Entradas de auditoría del Control Plane</caption>
              <thead>
                <tr>
                  <th scope="col">Fecha</th>
                  <th scope="col">Acción</th>
                  <th scope="col">Actor</th>
                  <th scope="col">Objetivo</th>
                  <th scope="col">Proyecto</th>
                  <th scope="col">Detalles</th>
                </tr>
              </thead>
              <tbody>
                {(page?.items ?? []).map((entry) => (
                  <AuditRow key={entry.id} entry={entry} />
                ))}
              </tbody>
            </table>
          </div>
        )}

        <div className="audit-pagination">
          <button
            type="button"
            className="button button--ghost button--small"
            disabled={offset === 0}
            onClick={() => setQuery((current) => ({ ...current, offset: Math.max(0, current.offset - current.limit) }))}
          >
            Anterior
          </button>
          <label className="audit-page-size" htmlFor="audit-page-size">
            Filas por página
            <select
              id="audit-page-size"
              name="pageSize"
              value={limit}
              onChange={(event) => changePageSize(Number(event.target.value))}
            >
              {pageSizes.map((size) => (
                <option key={size} value={size}>{size}</option>
              ))}
            </select>
          </label>
          <button
            type="button"
            className="button button--ghost button--small"
            disabled={last >= total}
            onClick={() => setQuery((current) => ({ ...current, offset: current.offset + current.limit }))}
          >
            Siguiente
          </button>
        </div>
      </div>

      <div className="leaf__group">
        <h2 className="leaf__group-title">Pasos de provisioning</h2>
        {!project ? (
          <p className="empty-inline">Selecciona un proyecto para ver sus pasos.</p>
        ) : operationsError ? (
          <Notice tone="error" title="No se pudo leer el historial">{operationsError}</Notice>
        ) : operations.length === 0 ? (
          <p className="empty-inline">Este proyecto todavía no tiene pasos registrados.</p>
        ) : (
          <div
            className="table-wrap audit-table-wrap"
            tabIndex={0}
            role="region"
            aria-labelledby="provisioning-caption"
          >
            <table className="data-table audit-table">
              <caption id="provisioning-caption" className="sr-only">
                Pasos de provisioning de {project.slug}
              </caption>
              <thead>
                <tr>
                  <th scope="col">Paso</th>
                  <th scope="col">Estado</th>
                  <th scope="col">Intento</th>
                  <th scope="col">Inicio</th>
                  <th scope="col">Fin</th>
                  <th scope="col">Error</th>
                </tr>
              </thead>
              <tbody>
                {operations.map((operation) => (
                  <tr key={operation.id}>
                    <th scope="row" data-label="Paso"><code>{operation.step}</code></th>
                    <td data-label="Estado"><span className="status">{operation.status}</span></td>
                    <td data-label="Intento">{operation.attempt}</td>
                    <td data-label="Inicio">{formatDate(operation.startedAt)}</td>
                    <td data-label="Fin">{operation.completedAt ? formatDate(operation.completedAt) : "—"}</td>
                    <td data-label="Error">
                      {operation.error ? <code className="audit-details">{operation.error}</code> : "—"}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </>
  );
}

function AuditRow({ entry }: { readonly entry: AuditEntry }) {
  return (
    <tr>
      <th scope="row" data-label="Fecha">{formatDate(entry.occurredAt)}</th>
      <td data-label="Acción"><code>{entry.action}</code><span className="cell-id">{shortId(entry.id)}</span></td>
      <td data-label="Actor">{entry.actor}</td>
      <td data-label="Objetivo">
        {entry.targetType}
        {entry.targetId ? <span className="cell-id">{shortId(entry.targetId)}</span> : null}
      </td>
      <td data-label="Proyecto">
        {entry.projectId ? <span className="mono">{shortId(entry.projectId)}</span> : "—"}
      </td>
      <td data-label="Detalles">
        {entry.details ? <code className="audit-details">{entry.details}</code> : "—"}
        {entry.correlationId ? <span className="cell-id">{shortId(entry.correlationId)}</span> : null}
      </td>
    </tr>
  );
}
