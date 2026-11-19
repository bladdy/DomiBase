import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import {
  ControlApiError,
  type ControlClient,
  type PolicyCommand,
  type PolicyExpression,
  type PolicyRequest,
  type PolicyRole,
  type Project,
  type RlsPolicy,
  type SchemaTable
} from "../lib/control-client";
import { errorText } from "../lib/format";
import { ConfirmDialog } from "./ConfirmDialog";
import { Notice } from "./Notice";

interface PoliciesSectionProps {
  readonly client: ControlClient;
  readonly project: Project | null;
  readonly tables: readonly SchemaTable[];
  readonly loadingTables: boolean;
  readonly tablesError: string | null;
}

interface PolicyFormState {
  name: string;
  commands: PolicyCommand[];
  roles: PolicyRole[];
  using: PolicyExpression;
  withCheck: PolicyExpression;
}

const commands: readonly PolicyCommand[] = ["SELECT", "INSERT", "UPDATE", "DELETE"];
const roles: readonly PolicyRole[] = ["anon", "authenticated"];

const expressions: readonly { value: PolicyExpression; label: string }[] = [
  { value: "false", label: "Denegar (false)" },
  { value: "true", label: "Permitir (true)" },
  {
    value: "project_id = current_setting('domibase.project_id', true)::uuid",
    label: "Solo filas del proyecto actual"
  }
];

function blankForm(): PolicyFormState {
  return { name: "", commands: [], roles: [], using: "false", withCheck: "false" };
}

function isExpression(value: string | null): value is PolicyExpression {
  return expressions.some((candidate) => candidate.value === value);
}

function toggle<T>(items: readonly T[], value: T): T[] {
  return items.includes(value) ? items.filter((item) => item !== value) : [...items, value];
}

function expressionLabel(value: string | null): string {
  return expressions.find((candidate) => candidate.value === value)?.label ?? value ?? "—";
}

function requestOf(form: PolicyFormState): PolicyRequest {
  return {
    name: form.name.trim(),
    commands: form.commands,
    roles: form.roles,
    using: form.using,
    withCheck: form.withCheck
  };
}

/**
 * RLS administration. The form cannot submit arbitrary SQL: commands, roles and expressions
 * are fixed options also enforced by the backend validator before any DDL is emitted.
 */
export function PoliciesSection({
  client,
  project,
  tables,
  loadingTables,
  tablesError
}: PoliciesSectionProps) {
  const [table, setTable] = useState("");
  const [policies, setPolicies] = useState<RlsPolicy[]>([]);
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  const [form, setForm] = useState<PolicyFormState>(blankForm);
  const [editing, setEditing] = useState<string | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<string | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  const projectId = project?.id;

  useEffect(() => () => abortRef.current?.abort(), []);

  useEffect(() => {
    setTable((current) =>
      tables.some((item) => item.name === current) ? current : (tables[0]?.name ?? "")
    );
  }, [tables]);

  const loadPolicies = useCallback(async () => {
    if (!projectId || table === "") {
      setPolicies([]);
      setLoading(false);
      return;
    }

    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;
    setLoading(true);
    setError(null);
    try {
      const result = await client.listPolicies(projectId, table, controller.signal);
      if (abortRef.current === controller) {
        setPolicies(result);
      }
    } catch (cause) {
      if (cause instanceof DOMException && cause.name === "AbortError") {
        return;
      }
      if (abortRef.current === controller) {
        setPolicies([]);
        setError(errorText(cause));
      }
    } finally {
      if (abortRef.current === controller) {
        abortRef.current = null;
        setLoading(false);
      }
    }
  }, [client, projectId, table]);

  useEffect(() => {
    setPolicies([]);
    setError(null);
    setStatus(null);
    setForm(blankForm());
    setEditing(null);
    setDeleteTarget(null);
    void loadPolicies();
  }, [loadPolicies, project?.id, table]);

  function startEdit(policy: RlsPolicy) {
    setEditing(policy.name);
    setStatus(null);
    setForm({
      name: policy.name,
      commands: policy.commands.filter((command): command is PolicyCommand =>
        commands.includes(command as PolicyCommand)
      ),
      roles: policy.roles.filter((role): role is PolicyRole => roles.includes(role as PolicyRole)),
      using: isExpression(policy.using) ? policy.using : "false",
      withCheck: isExpression(policy.withCheck) ? policy.withCheck : "false"
    });
  }

  function resetForm() {
    setForm(blankForm());
    setEditing(null);
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!project || table === "") {
      return;
    }

    const name = form.name.trim();
    if (!/^[a-z_][a-z0-9_]*$/.test(name) || name.length > 63) {
      setError("El nombre debe usar minúsculas, números y guiones bajos, y no superar 63 caracteres.");
      return;
    }
    if (form.commands.length === 0 || form.roles.length === 0) {
      setError("Selecciona al menos un comando y un rol funcional.");
      return;
    }

    setBusy(true);
    setError(null);
    setStatus(null);
    try {
      const input = requestOf(form);
      const result = editing
        ? await client.updatePolicy(project.id, table, editing, input)
        : await client.createPolicy(project.id, table, input);
      setStatus(`${result.action} · ${result.object ?? result.table}`);
      resetForm();
      await loadPolicies();
    } catch (cause) {
      if (cause instanceof ControlApiError && cause.hint) {
        setError(`${cause.message} ${cause.hint}`);
      } else {
        setError(errorText(cause));
      }
    } finally {
      setBusy(false);
    }
  }

  async function handleDelete() {
    if (!project || table === "" || deleteTarget === null) {
      return;
    }

    setBusy(true);
    setError(null);
    setStatus(null);
    const name = deleteTarget;
    try {
      await client.deletePolicy(project.id, table, name);
      setStatus(`policy.delete · ${name}`);
      if (editing === name) {
        resetForm();
      }
      setDeleteTarget(null);
      await loadPolicies();
    } catch (cause) {
      if (cause instanceof ControlApiError && cause.hint) {
        setError(`${cause.message} ${cause.hint}`);
      } else {
        setError(errorText(cause));
      }
      setDeleteTarget(null);
    } finally {
      setBusy(false);
    }
  }

  const ready = project?.status === "Ready";

  return (
    <section className="leaf__content" aria-labelledby="policies-heading">
      <div className="leaf__toolbar">
        <div>
          <h2 id="policies-heading" className="leaf__title">Políticas RLS</h2>
          <p className="leaf__subtitle">
            {project
              ? `${project.slug} · reglas de acceso por tabla`
              : "Selecciona un proyecto activo."}
          </p>
        </div>
      </div>

      {!project ? (
        <Notice title="Sin proyecto seleccionado">Abre la división Proyectos y elige uno.</Notice>
      ) : !ready ? (
        <Notice title="Proyecto no disponible">
          Las políticas solo se pueden administrar cuando el proyecto está en estado <code>Ready</code>.
        </Notice>
      ) : null}

      {ready ? (
        <>
          <Notice title="Plantillas seguras">
            Solo se pueden enviar los comandos, roles y expresiones permitidos por el servidor.
            No se acepta SQL libre. RLS permanece activo y el explorador de Datos verifica el efecto real.
          </Notice>

          {tablesError ? <Notice tone="error" title="No se pudo leer el esquema">{tablesError}</Notice> : null}

          <div className="field policy-table-picker">
            <label htmlFor="policy-table">Tabla</label>
            <select
              id="policy-table"
              value={table}
              onChange={(event) => setTable(event.target.value)}
              disabled={loadingTables || tables.length === 0}
            >
              {tables.length === 0 ? (
                <option value="">{loadingTables ? "Cargando…" : "Sin tablas"}</option>
              ) : (
                tables.map((item) => (
                  <option key={item.name} value={item.name}>
                    {item.name}
                  </option>
                ))
              )}
            </select>
          </div>

          {error ? <Notice tone="error" title="La operación no se pudo completar">{error}</Notice> : null}
          {status ? <p className="sql-meta" role="status">{status}</p> : null}

          <form className="form policy-form" onSubmit={handleSubmit} aria-busy={busy} noValidate>
            <div className="field">
              <label htmlFor="policy-name">Nombre de la política</label>
              <input
                id="policy-name"
                className="mono"
                value={form.name}
                onChange={(event) => setForm((current) => ({ ...current, name: event.target.value }))}
                disabled={editing !== null || table === ""}
                placeholder="contactos_select_anon"
              />
            </div>

            <fieldset className="policy-choice">
              <legend>Comandos</legend>
              {commands.map((command) => (
                <label key={command}>
                  <input
                    type="checkbox"
                    checked={form.commands.includes(command)}
                    onChange={() =>
                      setForm((current) => ({
                        ...current,
                        commands: toggle(current.commands, command)
                      }))
                    }
                  />
                  {command}
                </label>
              ))}
            </fieldset>

            <fieldset className="policy-choice">
              <legend>Roles funcionales</legend>
              {roles.map((role) => (
                <label key={role}>
                  <input
                    type="checkbox"
                    checked={form.roles.includes(role)}
                    onChange={() =>
                      setForm((current) => ({
                        ...current,
                        roles: toggle(current.roles, role)
                      }))
                    }
                  />
                  {role}
                </label>
              ))}
            </fieldset>

            <div className="field">
              <label htmlFor="policy-using">USING</label>
              <select
                id="policy-using"
                value={form.using}
                onChange={(event) =>
                  setForm((current) => ({
                    ...current,
                    using: event.target.value as PolicyExpression
                  }))
                }
              >
                {expressions.map((expression) => (
                  <option key={expression.value} value={expression.value}>
                    {expression.label}
                  </option>
                ))}
              </select>
            </div>

            <div className="field">
              <label htmlFor="policy-with-check">WITH CHECK</label>
              <select
                id="policy-with-check"
                value={form.withCheck}
                onChange={(event) =>
                  setForm((current) => ({
                    ...current,
                    withCheck: event.target.value as PolicyExpression
                  }))
                }
              >
                {expressions.map((expression) => (
                  <option key={expression.value} value={expression.value}>
                    {expression.label}
                  </option>
                ))}
              </select>
            </div>

            <div className="policy-form__actions">
              <button type="submit" className="button button--primary" disabled={busy || table === ""}>
                {busy ? "Guardando…" : editing ? "Guardar cambios" : "Crear política"}
              </button>
              {editing ? (
                <button type="button" className="button button--ghost" onClick={resetForm} disabled={busy}>
                  Cancelar edición
                </button>
              ) : null}
            </div>
          </form>

          <div className="leaf__group" aria-live="polite">
            <h3 className="leaf__group-title">Políticas de {table || "—"}</h3>
            {loading ? (
              <p className="loading" role="status">Leyendo políticas…</p>
            ) : policies.length === 0 ? (
              <p className="empty-inline">Esta tabla no tiene políticas RLS definidas.</p>
            ) : (
              <div className="policy-list">
                {policies.map((policy) => (
                  <article className="policy-card" key={policy.name}>
                    <header className="policy-card__head">
                      <h4 className="policy-card__name mono">{policy.name}</h4>
                      <div className="row-actions">
                        <button
                          type="button"
                          className="button button--ghost button--small"
                          onClick={() => startEdit(policy)}
                          disabled={busy}
                        >
                          Editar
                        </button>
                        <button
                          type="button"
                          className="button button--danger button--small"
                          onClick={() => setDeleteTarget(policy.name)}
                          disabled={busy}
                        >
                          Borrar
                        </button>
                      </div>
                    </header>
                    <dl className="policy-card__facts">
                      <div>
                        <dt>Comandos</dt>
                        <dd className="mono">{policy.commands.join(", ") || "—"}</dd>
                      </div>
                      <div>
                        <dt>Roles</dt>
                        <dd className="mono">{policy.roles.join(", ") || "—"}</dd>
                      </div>
                      <div>
                        <dt>USING</dt>
                        <dd>{expressionLabel(policy.using)}</dd>
                      </div>
                      <div>
                        <dt>WITH CHECK</dt>
                        <dd>{expressionLabel(policy.withCheck)}</dd>
                      </div>
                    </dl>
                  </article>
                ))}
              </div>
            )}
          </div>
        </>
      ) : null}

      <ConfirmDialog
        open={deleteTarget !== null}
        title="Borrar política RLS"
        confirmLabel="Borrar política"
        busy={busy}
        onClose={() => setDeleteTarget(null)}
        onConfirm={() => void handleDelete()}
      >
        <p>
          Se eliminará <code>{deleteTarget ?? ""}</code> de <code>{table}</code>. El cambio se aplica
          de inmediato a todas las peticiones públicas de ese proyecto.
        </p>
      </ConfirmDialog>
    </section>
  );
}
