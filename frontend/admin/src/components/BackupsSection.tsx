import { useState } from "react";
import type { ControlClient, Project, ProjectBackup } from "../lib/control-client";
import { errorText, formatDate, shortId } from "../lib/format";
import { ConfirmDialog } from "./ConfirmDialog";
import { Notice } from "./Notice";

interface BackupsSectionProps {
  readonly client: ControlClient;
  readonly project: Project | null;
  readonly backups: readonly ProjectBackup[];
  readonly loading: boolean;
  readonly error: string | null;
  readonly onChanged: () => void;
}

/**
 * Control Plane view for logical dumps. The browser sends no SQL, paths or credentials:
 * the server derives the owner role, stores the custom-format dump and verifies its hash.
 */
export function BackupsSection({ client, project, backups, loading, error, onChanged }: BackupsSectionProps) {
  const [busy, setBusy] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [pendingDelete, setPendingDelete] = useState<ProjectBackup | null>(null);
  const [pendingRestore, setPendingRestore] = useState<ProjectBackup | null>(null);
  const [typedSlug, setTypedSlug] = useState("");

  const ready = project?.status === "Ready";

  async function create() {
    if (!project) {
      return;
    }
    setBusy("create");
    setActionError(null);
    try {
      await client.createBackup(project.id);
      onChanged();
    } catch (cause) {
      setActionError(errorText(cause));
    } finally {
      setBusy(null);
    }
  }

  async function download(backup: ProjectBackup) {
    if (!project) {
      return;
    }
    setBusy(backup.id);
    setActionError(null);
    try {
      const blob = await client.downloadBackup(project.id, backup.id);
      const url = URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = url;
      link.download = `${backup.id}.dump`;
      document.body.append(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
    } catch (cause) {
      setActionError(errorText(cause));
    } finally {
      setBusy(null);
    }
  }

  async function confirmDelete() {
    if (!project || !pendingDelete) {
      return;
    }
    const backupId = pendingDelete.id;
    setBusy(backupId);
    setActionError(null);
    try {
      await client.deleteBackup(project.id, backupId);
      setPendingDelete(null);
      onChanged();
    } catch (cause) {
      setActionError(errorText(cause));
    } finally {
      setBusy(null);
    }
  }

  async function confirmRestore() {
    if (!project || !pendingRestore || typedSlug !== project.slug) {
      return;
    }
    const backupId = pendingRestore.id;
    setBusy(backupId);
    setActionError(null);
    try {
      await client.restoreBackup(project.id, backupId, project.slug);
      setPendingRestore(null);
      setTypedSlug("");
      onChanged();
    } catch (cause) {
      setActionError(errorText(cause));
    } finally {
      setBusy(null);
    }
  }

  return (
    <section className="leaf__content" aria-labelledby="backup-heading">
      <div className="leaf__toolbar">
        <div>
          <h2 id="backup-heading" className="leaf__title">Backups</h2>
          <p className="leaf__subtitle">
            {project ? `${project.slug} · copias lógicas por proyecto` : "Selecciona un proyecto en el cuaderno."}
          </p>
        </div>
        <button
          type="button"
          className="button button--primary button--small"
          disabled={!ready || busy !== null}
          onClick={() => void create()}
        >
          {busy === "create" ? "Creando…" : "Crear backup"}
        </button>
      </div>

      {!project ? (
        <Notice title="Sin proyecto seleccionado">Abre la división Proyectos y elige uno.</Notice>
      ) : !ready ? (
        <Notice title="Proyecto no disponible">
          Los backups solo se pueden gestionar cuando el proyecto está en estado <code>Ready</code>.
        </Notice>
      ) : null}

      {ready ? (
        <>
          <Notice title="Restauración deliberada">
            Cada copia se guarda con formato personalizado de PostgreSQL y una huella SHA-256. La restauración
            detiene el tráfico del Runtime, verifica la huella y reemplaza los objetos en una sola transacción;
            antes de continuar se pide escribir el <code>slug</code> del proyecto.
          </Notice>

          {error ? <Notice tone="error" title="No se pudo leer el historial">{error}</Notice> : null}
          {actionError ? <Notice tone="error" title="La operación no se pudo completar">{actionError}</Notice> : null}
          {loading ? <p className="loading" role="status">Cargando backups…</p> : null}

          {!loading && backups.length === 0 && !error ? (
            <p className="empty-inline">Todavía no hay copias para este proyecto.</p>
          ) : null}

          {backups.length > 0 ? (
            <div className="table-wrap" role="region" tabIndex={0} aria-labelledby="backup-table-caption">
              <table className="data-table data-table--dense" aria-labelledby="backup-table-caption">
                <caption id="backup-table-caption" className="sr-only">
                  Copias lógicas del proyecto {project.slug}
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Copia</th>
                    <th scope="col">Creada</th>
                    <th scope="col">Tamaño</th>
                    <th scope="col">Huella</th>
                    <th scope="col">Acciones</th>
                  </tr>
                </thead>
                <tbody>
                  {backups.map((backup) => (
                    <tr key={backup.id}>
                      <th scope="row" data-label="Copia">
                        <span className="mono">{shortId(backup.id)}</span>
                        <span className="cell-sub">{backup.format}</span>
                      </th>
                      <td data-label="Creada">{formatDate(backup.createdAt)}</td>
                      <td data-label="Tamaño" className="mono">{backup.sizeBytes.toLocaleString("es-ES")} B</td>
                      <td data-label="Huella"><span className="mono">{shortId(backup.sha256)}</span></td>
                      <td data-label="Acciones">
                        <div className="row-actions">
                          <button
                            type="button"
                            className="button button--ghost button--small"
                            aria-label={`Descargar backup ${shortId(backup.id)}`}
                            disabled={busy !== null}
                            onClick={() => void download(backup)}
                          >
                            Descargar
                          </button>
                          <button
                            type="button"
                            className="button button--ghost button--small"
                            aria-label={`Restaurar backup ${shortId(backup.id)}`}
                            disabled={busy !== null}
                            onClick={() => {
                              setTypedSlug("");
                              setPendingRestore(backup);
                            }}
                          >
                            Restaurar
                          </button>
                          <button
                            type="button"
                            className="button button--danger button--small"
                            aria-label={`Eliminar backup ${shortId(backup.id)}`}
                            disabled={busy !== null}
                            onClick={() => setPendingDelete(backup)}
                          >
                            Eliminar
                          </button>
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : null}
        </>
      ) : null}

      <ConfirmDialog
        open={pendingDelete !== null}
        title="Eliminar backup"
        confirmLabel="Eliminar"
        busy={busy !== null}
        onClose={() => setPendingDelete(null)}
        onConfirm={() => void confirmDelete()}
      >
        <p>
          Se borrarán el archivo y sus metadatos de <span className="mono">{shortId(pendingDelete?.id ?? "")}</span>.
          Esta acción no se puede deshacer.
        </p>
      </ConfirmDialog>

      <ConfirmDialog
        open={pendingRestore !== null}
        title="Restaurar proyecto"
        confirmLabel="Restaurar"
        busy={busy !== null}
        confirmDisabled={typedSlug !== project?.slug}
        onClose={() => {
          setPendingRestore(null);
          setTypedSlug("");
        }}
        onConfirm={() => void confirmRestore()}
      >
        <p>
          El estado del proyecto pasará a <code>Provisioning</code> mientras se sustituye la base y el Runtime
          rechazará peticiones. Escribe <span className="mono">{project?.slug}</span> para confirmar.
        </p>
        <div className="field">
          <label htmlFor="restore-confirm-slug">Slug del proyecto</label>
          <input
            id="restore-confirm-slug"
            value={typedSlug}
            autoComplete="off"
            onChange={(event) => setTypedSlug(event.target.value)}
          />
        </div>
      </ConfirmDialog>
    </section>
  );
}
