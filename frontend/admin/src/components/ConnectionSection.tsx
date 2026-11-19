import { useState } from "react";
import {
  type ApiKey,
  type ControlClient,
  type CreatedApiKey,
  type Project
} from "../lib/control-client";
import { errorText, formatDate } from "../lib/format";
import { Notice } from "./Notice";

interface ConnectionSectionProps {
  readonly client: ControlClient;
  readonly project: Project | null;
  readonly keys: readonly ApiKey[];
  readonly onKeysChanged: () => void;
}

async function copyText(value: string): Promise<void> {
  if (navigator.clipboard?.writeText) {
    await navigator.clipboard.writeText(value);
    return;
  }
  const area = document.createElement("textarea");
  area.value = value;
  area.style.position = "fixed";
  area.style.opacity = "0";
  document.body.appendChild(area);
  area.select();
  document.execCommand("copy");
  area.remove();
}

export function ConnectionSection({ client, project, keys, onKeysChanged }: ConnectionSectionProps) {
  const [created, setCreated] = useState<CreatedApiKey | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState<string | null>(null);

  if (!project) {
    return (
      <section className="leaf__content">
        <h2 className="leaf__title">Conexión</h2>
        <p className="leaf__subtitle">Selecciona un proyecto para ver sus datos de conexión.</p>
      </section>
    );
  }

  const activePublicKeys = keys.filter(
    (key) => key.kind === "Public" && key.revokedAt === null
  );
  const shownKey = created?.key ?? null;
  const keyForConfig = shownKey ?? "";

  async function handleCreateKey(): Promise<void> {
    setBusy(true);
    setError(null);
    try {
      const key = await client.createKey(project!.id, { kind: "Public", label: "panel" });
      setCreated(key);
      onKeysChanged();
    } catch (cause) {
      setError(errorText(cause));
    } finally {
      setBusy(false);
    }
  }

  async function handleCopy(label: string, value: string): Promise<void> {
    try {
      await copyText(value);
      setCopied(label);
    } catch (cause) {
      setError(errorText(cause));
    }
  }

  const config = JSON.stringify(
    {
      url: client.apiUrl,
      projectKey: keyForConfig || "<crea una clave pública>"
    },
    null,
    2
  );
  const example = `import { createClient } from "@domibase/client";

const db = createClient({
  url: ${JSON.stringify(client.apiUrl)},
  projectKey: ${JSON.stringify(keyForConfig || "db_public_xxx")}
});

const { data, error } = await db
  .from("contactos")
  .select("id,nombre,email")
  .order("created_at", { ascending: false })
  .limit(20);`;

  return (
    <section className="leaf__content" aria-labelledby="connection-heading">
      <div className="leaf__toolbar">
        <div>
          <h2 id="connection-heading" className="leaf__title">Conexión</h2>
          <p className="leaf__subtitle">
            Datos públicos para <span className="mono">{project.slug}</span>. Nunca incluye credenciales PostgreSQL.
          </p>
        </div>
      </div>

      {error ? <Notice tone="error" title="No se pudo completar la operación">{error}</Notice> : null}
      {copied ? <Notice title="Copiado al portapapeles">{copied}</Notice> : null}

      <div className="leaf__group">
        <h3 className="leaf__group-title">API</h3>
        <dl className="facts">
          <dt>URL de la API</dt>
          <dd className="mono">{client.apiUrl}</dd>
          <dt>API de datos</dt>
          <dd className="mono">/rest/v1/&lt;tabla&gt;</dd>
        </dl>
        <button type="button" className="button button--small" onClick={() => void handleCopy("URL de la API", client.apiUrl)}>
          Copiar URL
        </button>
      </div>

      <div className="leaf__group">
        <h3 className="leaf__group-title">Clave pública</h3>
        <p className="leaf__subtitle">
          La clave pública identifica el proyecto. El acceso a cada tabla depende de sus políticas RLS.
        </p>
        {shownKey ? (
          <div className="one-time">
            <p className="one-time__title">Cópiala ahora: no volverá a mostrarse completa.</p>
            <code className="one-time__value">{shownKey}</code>
            <button type="button" className="button button--small" onClick={() => void handleCopy("Clave pública", shownKey)}>
              Copiar clave
            </button>
          </div>
        ) : null}
        {activePublicKeys.length === 0 && !shownKey ? (
          <p className="empty-inline">Este proyecto todavía no tiene una clave pública activa.</p>
        ) : (
          <div className="table-wrap">
            <table className="data-table data-table--dense">
              <caption className="sr-only">Claves públicas activas</caption>
              <thead>
                <tr><th scope="col">Prefijo</th><th scope="col">Etiqueta</th><th scope="col">Creada</th></tr>
              </thead>
              <tbody>
                {activePublicKeys.map((key) => (
                  <tr key={key.id}>
                    <td data-label="Prefijo" className="mono">{key.prefix}…</td>
                    <td data-label="Etiqueta">{key.label ?? "—"}</td>
                    <td data-label="Creada" className="mono">{formatDate(key.createdAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <button type="button" className="button button--primary button--small" onClick={() => void handleCreateKey()} disabled={busy}>
          {busy ? "Creando…" : activePublicKeys.length === 0 ? "Crear clave pública" : "Rotar clave pública"}
        </button>
      </div>

      <div className="leaf__group">
        <h3 className="leaf__group-title">PostgreSQL</h3>
        <dl className="facts">
          <dt>Base de datos</dt>
          <dd className="mono">{project.databaseName}</dd>
          <dt>Esquema expuesto</dt>
          <dd className="mono">public</dd>
          <dt>Rol anónimo</dt>
          <dd className="mono">anon</dd>
          <dt>Rol autenticado</dt>
          <dd className="mono">authenticated</dd>
        </dl>
        <p className="leaf__subtitle">
          Las credenciales administrativas y la conexión privilegiada permanecen en el servidor.
        </p>
      </div>

      <div className="leaf__group">
        <div className="leaf__toolbar">
          <h3 className="leaf__group-title">Configuración del SDK</h3>
          <button type="button" className="button button--small" onClick={() => void handleCopy("Configuración JSON", config)}>
            Copiar configuración
          </button>
        </div>
        <pre className="code-block">{config}</pre>
      </div>

      <div className="leaf__group">
        <h3 className="leaf__group-title">Ejemplo de uso</h3>
        <pre className="code-block">{example}</pre>
      </div>
    </section>
  );
}
