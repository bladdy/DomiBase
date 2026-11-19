import { useState, type FormEvent } from "react";
import type { ApiKey, ControlClient, CreatedApiKey, Project } from "../lib/control-client";
import { errorText, formatDate, shortId } from "../lib/format";
import { ConfirmDialog } from "./ConfirmDialog";
import { Notice } from "./Notice";

interface KeysSectionProps {
  readonly client: ControlClient;
  readonly project: Project | null;
  readonly keys: readonly ApiKey[];
  readonly loading: boolean;
  readonly error: string | null;
  readonly onChanged: () => void;
}

export function KeysSection({ client, project, keys, loading, error, onChanged }: KeysSectionProps) {
  const [kind, setKind] = useState<"Public" | "Secret">("Public");
  const [label, setLabel] = useState("");
  const [createdKey, setCreatedKey] = useState<CreatedApiKey | null>(null);
  const [formError, setFormError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [pendingRevoke, setPendingRevoke] = useState<ApiKey | null>(null);
  const [revoking, setRevoking] = useState(false);

  const ready = project?.status === "Ready";

  async function handleCreate(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!project) {
      return;
    }
    setSubmitting(true);
    setFormError(null);
    try {
      const created = await client.createKey(project.id, {
        kind,
        ...(label.trim().length > 0 ? { label: label.trim() } : {})
      });
      setCreatedKey(created);
      setLabel("");
      onChanged();
    } catch (cause) {
      setFormError(errorText(cause));
    } finally {
      setSubmitting(false);
    }
  }

  async function handleRevoke() {
    if (!project || !pendingRevoke) {
      return;
    }
    setRevoking(true);
    setFormError(null);
    try {
      await client.revokeKey(project.id, pendingRevoke.id);
      if (createdKey?.id === pendingRevoke.id) {
        setCreatedKey(null);
      }
      setPendingRevoke(null);
      onChanged();
    } catch (cause) {
      setFormError(errorText(cause));
    } finally {
      setRevoking(false);
    }
  }

  return (
    <section className="leaf__content" aria-labelledby="keys-heading">
      <div className="leaf__toolbar">
        <div>
          <h2 id="keys-heading" className="leaf__title">Claves</h2>
          <p className="leaf__subtitle">
            {project ? `${project.slug} · ${project.status}` : "Selecciona un proyecto en el cuaderno."}
          </p>
        </div>
      </div>

      {!project ? (
        <Notice title="Sin proyecto seleccionado">Abre la división Proyectos y elige uno.</Notice>
      ) : !ready ? (
        <Notice title="Provisioning pendiente">
          Las claves solo se pueden emitir cuando el proyecto está en estado <code>Ready</code>.
        </Notice>
      ) : null}

      {ready ? (
        <form className="form form--inline" onSubmit={handleCreate} noValidate>
          <div className="field">
            <label htmlFor="key-kind">Tipo</label>
            <select id="key-kind" value={kind} onChange={(event) => setKind(event.target.value as "Public" | "Secret")}>
              <option value="Public">Pública (db_public_…)</option>
              <option value="Secret">Secreta (db_secret_…)</option>
            </select>
          </div>
          <div className="field field--grow">
            <label htmlFor="key-label">Etiqueta (opcional)</label>
            <input
              id="key-label"
              value={label}
              onChange={(event) => {
                setLabel(event.target.value);
                setFormError(null);
              }}
              placeholder="astro-production"
            />
          </div>
          <button type="submit" className="button button--primary" disabled={submitting}>
            {submitting ? "Generando…" : "Emitir clave"}
          </button>
        </form>
      ) : null}

      {createdKey && project && createdKey.projectId === project.id ? (
        <div className="one-time" role="status">
          <div>
            <strong className="one-time__title">Cópiala ahora. No volverá a mostrarse.</strong>
            <p className="one-time__hint">
              Solo el valor completo aparece en este momento; el panel no lo guarda.
            </p>
          </div>
          <code className="one-time__value">{createdKey.key}</code>
          <button
            type="button"
            className="button button--ghost button--small"
            onClick={() => {
              void navigator.clipboard
                ?.writeText(createdKey.key)
                .catch(() => setFormError("No se pudo copiar. Selecciona el valor manualmente."));
            }}
          >
            Copiar
          </button>
        </div>
      ) : null}

      {formError ? <Notice tone="error" title="La operación falló">{formError}</Notice> : null}
      {error ? <Notice tone="error" title="No se pudieron cargar las claves">{error}</Notice> : null}

      {loading ? (
        <p className="loading" role="status">Leyendo claves…</p>
      ) : keys.length === 0 ? (
        <div className="empty">
          <p className="empty__title">Sin claves emitidas.</p>
          <p>Una clave pública identifica el proyecto; no concede acceso a los datos por sí sola.</p>
        </div>
      ) : (
        <div className="table-wrap">
          <table className="data-table">
            <caption className="sr-only">Claves del proyecto seleccionado</caption>
            <thead>
              <tr>
                <th scope="col">Prefijo</th>
                <th scope="col">Tipo</th>
                <th scope="col">Etiqueta</th>
                <th scope="col">Creada</th>
                <th scope="col">Último uso</th>
                <th scope="col">Acciones</th>
              </tr>
            </thead>
            <tbody>
              {keys.map((apiKey) => (
                <tr key={apiKey.id} className={apiKey.revokedAt ? "row--revoked" : undefined}>
                  <td data-label="Prefijo"><span className="mono strong">{apiKey.prefix}</span></td>
                  <td data-label="Tipo">{apiKey.kind}</td>
                  <td data-label="Etiqueta">{apiKey.label ?? <span className="muted">—</span>}</td>
                  <td data-label="Creada" className="mono">{formatDate(apiKey.createdAt)}</td>
                  <td data-label="Último uso">
                    {apiKey.lastUsedAt ? <span className="mono">{formatDate(apiKey.lastUsedAt)}</span> : <span className="muted">Nunca</span>}
                    {apiKey.revokedAt ? (
                      <span className="revoked-note mono" title={apiKey.id}>
                        Revocada · {shortId(apiKey.id)}
                      </span>
                    ) : null}
                  </td>
                  <td data-label="Acciones">
                    <button
                      type="button"
                      className="button button--ghost button--small"
                      onClick={() => setPendingRevoke(apiKey)}
                      disabled={Boolean(apiKey.revokedAt) || !ready}
                    >
                      Revocar
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <ConfirmDialog
        open={pendingRevoke !== null}
        title="Revocar clave"
        confirmLabel="Revocar definitivamente"
        busy={revoking}
        onClose={() => setPendingRevoke(null)}
        onConfirm={() => void handleRevoke()}
      >
        <p>
          La clave <code className="mono">{pendingRevoke?.prefix}</code> dejará de funcionar de inmediato.
          Esta acción no se puede deshacer.
        </p>
      </ConfirmDialog>
    </section>
  );
}
