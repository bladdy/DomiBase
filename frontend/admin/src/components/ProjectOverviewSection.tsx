import type { Project } from "../lib/control-client";
import { formatDate } from "../lib/format";
import { Notice } from "./Notice";
import { StatusPill } from "./StatusPill";

interface ProjectOverviewSectionProps {
  readonly project: Project | null;
  readonly error: string | null;
  readonly onRetry: (id: string) => void;
  readonly onDelete: (project: Project) => void;
  readonly onOpenTables: () => void;
  readonly onOpenConnection: () => void;
}

export function ProjectOverviewSection({
  project,
  error,
  onRetry,
  onDelete,
  onOpenTables,
  onOpenConnection
}: ProjectOverviewSectionProps) {
  if (!project) {
    return (
      <section className="leaf__content" aria-labelledby="overview-heading">
        <h2 id="overview-heading" className="leaf__title">Resumen</h2>
        <p className="leaf__subtitle">Selecciona o crea un proyecto para ver su estado.</p>
        {error ? <Notice tone="error" title="No se pudo cargar el proyecto">{error}</Notice> : null}
      </section>
    );
  }

  return (
    <section className="leaf__content" aria-labelledby="overview-heading">
      <div className="leaf__toolbar">
        <div>
          <h2 id="overview-heading" className="leaf__title">{project.name}</h2>
          <p className="leaf__subtitle">
            <span className="mono">{project.slug}</span> · <StatusPill status={project.status} />
          </p>
        </div>
        <div className="row-actions">
          <button type="button" className="button" onClick={onOpenConnection}>Ver conexión</button>
          <button type="button" className="button button--primary" onClick={onOpenTables}>
            Abrir Table Editor
          </button>
        </div>
      </div>

      {error ? <Notice tone="error" title="No se pudo actualizar el proyecto">{error}</Notice> : null}

      <div className="stat-grid">
        <article className="stat-card">
          <p className="stat-card__label">Base de datos</p>
          <p className="stat-card__value stat-card__value--mono">{project.databaseName}</p>
        </article>
        <article className="stat-card">
          <p className="stat-card__label">Estado</p>
          <p className="stat-card__value"><StatusPill status={project.status} /></p>
        </article>
        <article className="stat-card">
          <p className="stat-card__label">Creado</p>
          <p className="stat-card__value">{formatDate(project.createdAt)}</p>
        </article>
        <article className="stat-card">
          <p className="stat-card__label">Límite de peticiones</p>
          <p className="stat-card__value">
            {project.effectiveRateLimitPermits} / {project.effectiveRateLimitWindowSeconds} s
          </p>
        </article>
      </div>

      {project.failureReason ? (
        <Notice tone="error" title="Último error de provisioning">
          {project.failureReason}
        </Notice>
      ) : null}

      <div className="leaf__actions">
        {project.status === "Failed" ? (
          <button type="button" className="button" onClick={() => onRetry(project.id)}>
            Reintentar provisioning
          </button>
        ) : null}
        <button type="button" className="button button--danger" onClick={() => onDelete(project)}>
          Eliminar proyecto
        </button>
      </div>
    </section>
  );
}
