import type { Bucket, Project } from "../lib/control-client";
import { formatDate, shortId } from "../lib/format";
import { Notice } from "./Notice";

interface BucketsSectionProps {
  readonly project: Project | null;
  readonly buckets: readonly Bucket[];
  readonly loading: boolean;
  readonly error: string | null;
}

export function BucketsSection({ project, buckets, loading, error }: BucketsSectionProps) {
  return (
    <section className="leaf__content" aria-labelledby="buckets-heading">
      <div className="leaf__toolbar">
        <div>
          <h2 id="buckets-heading" className="leaf__title">Buckets</h2>
          <p className="leaf__subtitle">
            {project ? `${project.slug} · solo lectura` : "Selecciona un proyecto en el cuaderno."}
          </p>
        </div>
      </div>

      {!project ? <Notice title="Sin proyecto seleccionado">Abre la división Proyectos y elige uno.</Notice> : null}
      {error ? <Notice tone="error" title="No se pudieron cargar los buckets">{error}</Notice> : null}

      {loading ? (
        <p className="loading" role="status">Leyendo buckets…</p>
      ) : buckets.length === 0 ? (
        <div className="empty">
          <p className="empty__title">Sin buckets registrados.</p>
          <p>Los buckets creados desde la API de control aparecerán aquí.</p>
        </div>
      ) : (
        <div className="table-wrap">
          <table className="data-table">
            <caption className="sr-only">Buckets del proyecto seleccionado</caption>
            <thead>
              <tr>
                <th scope="col">Nombre</th>
                <th scope="col">Acceso</th>
                <th scope="col">Creado</th>
                <th scope="col">Identificador</th>
              </tr>
            </thead>
            <tbody>
              {buckets.map((bucket) => (
                <tr key={bucket.id}>
                  <td data-label="Nombre"><span className="mono strong">{bucket.name}</span></td>
                  <td data-label="Acceso">
                    <span className={`access ${bucket.publicAccess ? "access--public" : ""}`}>
                      {bucket.publicAccess ? "Público" : "Privado"}
                    </span>
                  </td>
                  <td data-label="Creado" className="mono">{formatDate(bucket.createdAt)}</td>
                  <td data-label="Identificador"><span className="mono" title={bucket.id}>{shortId(bucket.id)}</span></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
