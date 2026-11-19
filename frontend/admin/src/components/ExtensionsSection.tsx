import { useState } from "react";
import type { ControlClient, PostgresExtension, Project } from "../lib/control-client";
import { errorText } from "../lib/format";
import { ConfirmDialog } from "./ConfirmDialog";
import { Notice } from "./Notice";

interface ExtensionsSectionProps {
  readonly client: ControlClient;
  readonly project: Project | null;
  readonly extensions: readonly PostgresExtension[];
  readonly loading: boolean;
  readonly error: string | null;
  readonly onChanged: () => void;
}

/**
 * Administrative catalog of trusted PostgreSQL extensions. The server owns the allowlist,
 * the DDL and the audit trail; this view only sends an extension name and refreshes the
 * project after each operation.
 */
export function ExtensionsSection({
  client,
  project,
  extensions,
  loading,
  error,
  onChanged
}: ExtensionsSectionProps) {
  const [busyName, setBusyName] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [pendingUninstall, setPendingUninstall] = useState<PostgresExtension | null>(null);

  const ready = project?.status === "Ready";

  async function install(extension: PostgresExtension) {
    if (!project) {
      return;
    }
    setBusyName(extension.name);
    setActionError(null);
    try {
      await client.installExtension(project.id, extension.name);
      onChanged();
    } catch (cause) {
      setActionError(errorText(cause));
    } finally {
      setBusyName(null);
    }
  }

  async function confirmUninstall() {
    if (!project || !pendingUninstall) {
      return;
    }
    const name = pendingUninstall.name;
    setBusyName(name);
    setActionError(null);
    try {
      await client.uninstallExtension(project.id, name);
      setPendingUninstall(null);
      onChanged();
    } catch (cause) {
      setActionError(errorText(cause));
    } finally {
      setBusyName(null);
    }
  }

  return (
    <section className="leaf__content" aria-labelledby="extension-heading">
      <div className="leaf__toolbar">
        <div>
          <h2 id="extension-heading" className="leaf__title">Extensiones</h2>
          <p className="leaf__subtitle">
            {project
              ? `${project.slug} · catálogo permitido`
              : "Selecciona un proyecto en el cuaderno."}
          </p>
        </div>
      </div>

      {!project ? (
        <Notice title="Sin proyecto seleccionado">Abre la división Proyectos y elige uno.</Notice>
      ) : !ready ? (
        <Notice title="Proyecto no disponible">
          Las extensiones solo se pueden gestionar cuando el proyecto está en estado <code>Ready</code>.
        </Notice>
      ) : null}

      {ready ? (
        <>
          <Notice title="Catálogo acotado">
            Solo se muestran extensiones <code>trusted</code> permitidas por el servidor. El panel envía
            únicamente el nombre; PostgreSQL ejecuta el DDL como propietario del proyecto y la operación
            queda registrada en la auditoría.
          </Notice>

          {error ? <Notice tone="error" title="No se pudo leer el catálogo">{error}</Notice> : null}
          {actionError ? <Notice tone="error" title="La operación no se pudo completar">{actionError}</Notice> : null}
          {loading ? <p className="loading" role="status">Cargando catálogo…</p> : null}

          {!loading && extensions.length === 0 && !error ? (
            <p className="empty-inline">No hay extensiones disponibles en este servidor.</p>
          ) : null}

          {extensions.length > 0 ? (
            <div
              className="table-wrap"
              role="region"
              tabIndex={0}
              aria-labelledby="extension-table-caption"
            >
              <table className="data-table" aria-labelledby="extension-table-caption">
                <caption id="extension-table-caption" className="sr-only">
                  Extensiones PostgreSQL del proyecto
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Extensión</th>
                    <th scope="col">Versión</th>
                    <th scope="col">Estado</th>
                    <th scope="col">Acciones</th>
                  </tr>
                </thead>
                <tbody>
                  {extensions.map((extension) => (
                    <tr key={extension.name}>
                      <td data-label="Extensión">
                        <span className="strong mono">{extension.name}</span>
                        <span className="cell-sub">{extension.description}</span>
                      </td>
                      <td data-label="Versión">
                        <span className="mono">{extension.installedVersion ?? extension.availableVersion ?? "—"}</span>
                      </td>
                      <td data-label="Estado">
                        {extension.installed
                          ? `Instalada${extension.trusted ? " · trusted" : ""}`
                          : extension.available
                            ? `Disponible${extension.trusted ? " · trusted" : ""}`
                            : "No disponible"}
                      </td>
                      <td data-label="Acciones">
                        <div className="row-actions">
                          {extension.installed ? (
                            <button
                              type="button"
                              className="button button--danger button--small"
                              aria-label={`Desinstalar ${extension.name}`}
                              disabled={busyName !== null}
                              onClick={() => setPendingUninstall(extension)}
                            >
                              Desinstalar
                            </button>
                          ) : extension.canInstall ? (
                            <button
                              type="button"
                              className="button button--primary button--small"
                              aria-label={`Instalar ${extension.name}`}
                              disabled={busyName !== null}
                              onClick={() => void install(extension)}
                            >
                              {busyName === extension.name ? "Instalando…" : "Instalar"}
                            </button>
                          ) : (
                            <span className="muted">—</span>
                          )}
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
        open={pendingUninstall !== null}
        title="Desinstalar extensión"
        confirmLabel="Desinstalar"
        busy={busyName !== null}
        onClose={() => setPendingUninstall(null)}
        onConfirm={() => void confirmUninstall()}
      >
        <p>
          Se eliminará <span className="mono">{pendingUninstall?.name}</span> de la base del proyecto.
          PostgreSQL rechazará la operación si todavía existen objetos que dependen de ella.
        </p>
      </ConfirmDialog>
    </section>
  );
}
