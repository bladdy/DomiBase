import { useEffect, useId, useRef, useState, type FormEvent } from "react";
import type { ControlClient, Project } from "../lib/control-client";
import { errorText, formatDate, shortId } from "../lib/format";
import { Notice } from "./Notice";
import { StatusPill } from "./StatusPill";

interface ProjectsSectionProps {
  readonly client: ControlClient;
  readonly projects: readonly Project[];
  readonly loading: boolean;
  readonly error: string | null;
  readonly selectedProjectId: string | null;
  readonly onSelectProject: (id: string) => void;
  readonly onOpenProject: (id: string) => void;
  readonly onRequestDelete: (project: Project) => void;
  readonly onChanged: () => void;
}

export function ProjectsSection({
  client,
  projects,
  loading,
  error,
  selectedProjectId,
  onSelectProject,
  onOpenProject,
  onRequestDelete,
  onChanged
}: ProjectsSectionProps) {
  const [showCreate, setShowCreate] = useState(false);
  const [slug, setSlug] = useState("");
  const [name, setName] = useState("");
  const [formError, setFormError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [retryingId, setRetryingId] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [limitProject, setLimitProject] = useState<Project | null>(null);
  const [permitsText, setPermitsText] = useState("");
  const [windowText, setWindowText] = useState("");
  const [savingLimit, setSavingLimit] = useState(false);
  const [limitError, setLimitError] = useState<string | null>(null);
  const limitDialogRef = useRef<HTMLDialogElement>(null);
  const limitTitleId = useId();

  useEffect(() => {
    const dialog = limitDialogRef.current;
    if (!dialog) {
      return;
    }
    if (limitProject && !dialog.open) {
      dialog.showModal();
    } else if (!limitProject && dialog.open) {
      dialog.close();
    }
  }, [limitProject]);

  async function handleCreate(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (slug.trim().length === 0 || name.trim().length === 0) {
      setFormError("El slug y el nombre son obligatorios.");
      return;
    }
    setSubmitting(true);
    setFormError(null);
    try {
      const created = await client.createProject({ slug: slug.trim(), name: name.trim() });
      setSlug("");
      setName("");
      setShowCreate(false);
      setNotice(`${created.slug} está en ${created.status}. La base asignada es ${created.databaseName}.`);
      onSelectProject(created.id);
      onChanged();
    } catch (cause) {
      setFormError(errorText(cause));
    } finally {
      setSubmitting(false);
    }
  }

  async function handleRetry(project: Project) {
    setRetryingId(project.id);
    setNotice(null);
    try {
      const updated = await client.retryProvisioning(project.id);
      setNotice(`Reintento iniciado para ${project.slug}. Estado actual: ${updated.status}.`);
      onChanged();
    } catch (cause) {
      setFormError(errorText(cause));
    } finally {
      setRetryingId(null);
    }
  }

  function openLimitDialog(project: Project) {
    setLimitProject(project);
    setPermitsText(String(project.rateLimitPermits ?? project.effectiveRateLimitPermits));
    setWindowText(String(project.rateLimitWindowSeconds ?? project.effectiveRateLimitWindowSeconds));
    setLimitError(null);
  }

  function closeLimitDialog() {
    if (savingLimit) {
      return;
    }
    setLimitProject(null);
    setLimitError(null);
  }

  async function handleSaveLimit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!limitProject) {
      return;
    }

    const permits = permitsText.trim() === "" ? null : Number(permitsText);
    const windowSeconds = windowText.trim() === "" ? null : Number(windowText);

    if (
      permits !== null && (!Number.isInteger(permits) || permits < 1 || permits > 10000) ||
      windowSeconds !== null && (!Number.isInteger(windowSeconds) || windowSeconds < 1 || windowSeconds > 3600)
    ) {
      setLimitError("Cupo: 1–10000. Ventana: 1–3600 s. Deja el campo vacío para heredar el default.");
      return;
    }

    setSavingLimit(true);
    setLimitError(null);
    try {
      const updated = await client.updateRateLimit(limitProject.id, { permits, windowSeconds });
      setNotice(
        `${updated.slug}: ${updated.effectiveRateLimitPermits} peticiones / ${updated.effectiveRateLimitWindowSeconds} s.`
      );
      setLimitProject(null);
      onChanged();
    } catch (cause) {
      setLimitError(errorText(cause));
    } finally {
      setSavingLimit(false);
    }
  }

  return (
    <section className="leaf__content" aria-labelledby="projects-heading">
      <div className="leaf__toolbar">
        <div>
          <h2 id="projects-heading" className="leaf__title">
            Proyectos
          </h2>
          <p className="leaf__subtitle">Estado real del provisioning y bases asignadas.</p>
        </div>
        <button type="button" className="button button--primary" onClick={() => setShowCreate((open) => !open)}>
          {showCreate ? "Cerrar formulario" : "Nuevo proyecto"}
        </button>
      </div>

      {showCreate ? (
        <form className="form form--inline" onSubmit={handleCreate} noValidate>
          <div className="field">
            <label htmlFor="project-slug">Slug</label>
            <input
              id="project-slug"
              value={slug}
              onChange={(event) => {
                setSlug(event.target.value);
                setFormError(null);
              }}
              placeholder="paneles-solares"
              spellCheck={false}
              aria-invalid={formError ? "true" : undefined}
              aria-describedby={formError ? "project-form-error" : undefined}
            />
          </div>
          <div className="field">
            <label htmlFor="project-name">Nombre</label>
            <input
              id="project-name"
              value={name}
              onChange={(event) => {
                setName(event.target.value);
                setFormError(null);
              }}
              placeholder="Paneles Solares"
              aria-invalid={formError ? "true" : undefined}
              aria-describedby={formError ? "project-form-error" : undefined}
            />
          </div>
          <button type="submit" className="button button--primary" disabled={submitting}>
            {submitting ? "Creando…" : "Crear proyecto"}
          </button>
        </form>
      ) : null}

      {formError ? (
        <p id="project-form-error" className="notice notice--error" role="alert">
          {formError}
        </p>
      ) : null}
      {notice ? <Notice title="Operación completada">{notice}</Notice> : null}
      {error ? <Notice tone="error" title="No se pudieron cargar los proyectos">{error}</Notice> : null}

      {loading ? (
        <p className="loading" role="status">Leyendo proyectos…</p>
      ) : projects.length === 0 ? (
        <div className="empty">
          <p className="empty__title">Todavía no hay proyectos.</p>
          <p>Crea el primero para provisionar una base de datos independiente.</p>
        </div>
      ) : (
        <div className="table-wrap">
          <table className="data-table">
            <caption className="sr-only">Proyectos del Control Plane</caption>
            <thead>
              <tr>
                <th scope="col">Proyecto</th>
                <th scope="col">Estado</th>
                <th scope="col">Base de datos</th>
                <th scope="col">Cupo</th>
                <th scope="col">Creado</th>
                <th scope="col">Acciones</th>
              </tr>
            </thead>
            <tbody>
              {projects.map((project) => (
                <tr key={project.id} className={project.id === selectedProjectId ? "row--selected" : undefined}>
                  <td data-label="Proyecto">
                    <span className="mono strong">{project.slug}</span>
                    <span className="cell-sub">{project.name}</span>
                    <span className="cell-id mono" title={project.id}>{shortId(project.id)}</span>
                  </td>
                  <td data-label="Estado">
                    <StatusPill status={project.status} />
                    {project.failureReason ? (
                      <span className="errata">
                        <span className="errata__tag">Errata</span>
                        {project.failureReason}
                      </span>
                    ) : null}
                  </td>
                  <td data-label="Base de datos"><span className="mono">{project.databaseName}</span></td>
                  <td data-label="Cupo">
                    <span className="mono strong">{project.effectiveRateLimitPermits}</span>
                    <span className="cell-sub">/ {project.effectiveRateLimitWindowSeconds} s</span>
                    <span className="cell-sub">
                      {project.rateLimitPermits === null && project.rateLimitWindowSeconds === null
                        ? "Heredado"
                        : "Personalizado"}
                    </span>
                  </td>
                  <td data-label="Creado" className="mono">{formatDate(project.createdAt)}</td>
                  <td data-label="Acciones">
                    <div className="row-actions">
                      <button
                        type="button"
                        className="button button--primary button--small"
                        onClick={() => onOpenProject(project.id)}
                      >
                        Abrir
                      </button>
                      <button
                        type="button"
                        className="button button--ghost button--small"
                        onClick={() => onSelectProject(project.id)}
                        disabled={project.id === selectedProjectId}
                      >
                        {project.id === selectedProjectId ? "Activo" : "Seleccionar"}
                      </button>
                      <button
                        type="button"
                        className="button button--ghost button--small"
                        onClick={() => openLimitDialog(project)}
                      >
                        Editar
                      </button>
                      <button
                        type="button"
                        className="button button--danger button--small"
                        onClick={() => onRequestDelete(project)}
                      >
                        Eliminar
                      </button>
                      {project.status === "Failed" ? (
                        <button
                          type="button"
                          className="button button--small button--warn"
                          onClick={() => void handleRetry(project)}
                          disabled={retryingId === project.id}
                        >
                          {retryingId === project.id ? "Reintentando…" : "Reintentar"}
                        </button>
                      ) : null}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <dialog
        ref={limitDialogRef}
        className="confirm"
        aria-labelledby={limitTitleId}
        onCancel={(event) => {
          event.preventDefault();
          closeLimitDialog();
        }}
        onClick={(event) => {
          if (event.target === limitDialogRef.current) {
            closeLimitDialog();
          }
        }}
      >
        <div className="confirm__sheet">
          <h2 id={limitTitleId} className="confirm__title">Editar proyecto</h2>
          <form className="form" onSubmit={(event) => void handleSaveLimit(event)} noValidate>
            <div className="confirm__body">
              <p>
                Proyecto <span className="mono">{limitProject?.slug}</span>. Un cupo es el número de
                peticiones permitidas por ventana. Deja un campo vacío para heredar el default global.
              </p>
              <div className="field">
                <label htmlFor="project-rate-permits">Peticiones por ventana</label>
                <input
                  id="project-rate-permits"
                  type="number"
                  min={1}
                  max={10000}
                  value={permitsText}
                  onChange={(event) => {
                    setPermitsText(event.target.value);
                    setLimitError(null);
                  }}
                />
              </div>
              <div className="field">
                <label htmlFor="project-rate-window">Ventana (segundos)</label>
                <input
                  id="project-rate-window"
                  type="number"
                  min={1}
                  max={3600}
                  value={windowText}
                  onChange={(event) => {
                    setWindowText(event.target.value);
                    setLimitError(null);
                  }}
                />
              </div>
              {limitError ? (
                <p className="notice notice--error" role="alert">{limitError}</p>
              ) : null}
            </div>
            <div className="confirm__actions">
              <button type="button" className="button button--ghost" onClick={closeLimitDialog} disabled={savingLimit}>
                Cancelar
              </button>
              <button type="submit" className="button button--primary" disabled={savingLimit}>
                {savingLimit ? "Guardando…" : "Guardar cupo"}
              </button>
            </div>
          </form>
        </div>
      </dialog>
    </section>
  );
}
