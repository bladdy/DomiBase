import { useCallback, useEffect, useRef, useState } from "react";
import { ConfirmDialog } from "./components/ConfirmDialog";
import { ConnectionSection } from "./components/ConnectionSection";
import { LoginGate } from "./components/LoginGate";
import { ProjectOverviewSection } from "./components/ProjectOverviewSection";
import { ProjectsSection } from "./components/ProjectsSection";
import { TabRail, type ProjectSectionId, type SectionId, type TabItem } from "./components/TabRail";
import { TableEditorSection } from "./components/TableEditorSection";
import {
  ControlApiError,
  ControlClient,
  type ApiKey,
  type Project,
  type SchemaTable
} from "./lib/control-client";
import { errorText } from "./lib/format";
import { clearSessionToken, readSessionToken, writeSessionToken } from "./lib/session";

const sectionTitles: Record<SectionId, string> = {
  projects: "Proyectos",
  overview: "Resumen",
  tables: "Table Editor",
  connection: "Conexión"
};

const projectTabs: readonly TabItem[] = [
  { id: "overview", label: "Resumen", count: 0 },
  { id: "tables", label: "Table Editor", count: 0 },
  { id: "connection", label: "Conexión", count: 0 }
];

function isAuthFailure(error: unknown): boolean {
  return (
    error instanceof ControlApiError &&
    (error.status === 401 || error.code === "UNAUTHORIZED" || error.code === "NO_SESSION")
  );
}

export default function App() {
  const [client] = useState(() => {
    const saved = readSessionToken();
    return new ControlClient(saved ? { token: saved } : {});
  });
  const [token, setToken] = useState<string | null>(() => client.token);
  const [authBusy, setAuthBusy] = useState(() => token !== null);
  const [sessionError, setSessionError] = useState<string | null>(null);
  const [section, setSection] = useState<SectionId>("projects");

  const [projects, setProjects] = useState<Project[]>([]);
  const [keys, setKeys] = useState<ApiKey[]>([]);
  const [tables, setTables] = useState<SchemaTable[]>([]);
  const [selectedProjectId, setSelectedProjectId] = useState<string | null>(null);
  const selectedProjectRef = useRef<string | null>(null);
  const resourceGeneration = useRef(0);
  const [loading, setLoading] = useState({ projects: false, keys: false, tables: false });
  const [errors, setErrors] = useState<{ projects: string | null; keys: string | null; tables: string | null }>({
    projects: null,
    keys: null,
    tables: null
  });
  const [signOutConfirm, setSignOutConfirm] = useState(false);
  const [projectToDelete, setProjectToDelete] = useState<Project | null>(null);
  const [deleteSlug, setDeleteSlug] = useState("");
  const [deleteBusy, setDeleteBusy] = useState(false);

  const selectProject = useCallback((id: string | null) => {
    selectedProjectRef.current = id;
    setSelectedProjectId(id);
  }, []);

  const signOut = useCallback(
    (message?: string) => {
      client.setToken(null);
      clearSessionToken();
      setToken(null);
      setSessionError(message ?? null);
      setAuthBusy(false);
      selectProject(null);
      setProjects([]);
      setKeys([]);
      setTables([]);
      setLoading({ projects: false, keys: false, tables: false });
      setErrors({ projects: null, keys: null, tables: null });
      setSection("projects");
      setSignOutConfirm(false);
      setProjectToDelete(null);
    },
    [client, selectProject]
  );

  const refreshProjects = useCallback(
    async (preferredId?: string): Promise<Project[]> => {
      setLoading((current) => ({ ...current, projects: true }));
      try {
        const list = await client.listProjects();
        const currentId = preferredId ?? selectedProjectRef.current;
        const chosen =
          currentId && list.some((project) => project.id === currentId)
            ? currentId
            : (list.find((project) => project.status === "Ready")?.id ?? list[0]?.id ?? null);
        selectProject(chosen);
        setProjects(list);
        setErrors((current) => ({ ...current, projects: null }));
        return list;
      } catch (error) {
        if (isAuthFailure(error)) {
          signOut(errorText(error));
          return [];
        }
        setErrors((current) => ({ ...current, projects: errorText(error) }));
        return [];
      } finally {
        setLoading((current) => ({ ...current, projects: false }));
      }
    },
    [client, selectProject, signOut]
  );

  const refreshResources = useCallback(
    async (projectId: string | null): Promise<void> => {
      const generation = ++resourceGeneration.current;
      if (!projectId) {
        setKeys([]);
        setTables([]);
        return;
      }

      setLoading((current) => ({ ...current, keys: true, tables: true }));
      try {
        const [keyResult, tableResult] = await Promise.allSettled([
          client.listKeys(projectId),
          client.listSchemaTables(projectId)
        ]);
        if (generation !== resourceGeneration.current) {
          return;
        }

        const rejected = [keyResult, tableResult].find((result) => result.status === "rejected");
        if (rejected?.status === "rejected" && isAuthFailure(rejected.reason)) {
          signOut(errorText(rejected.reason));
          return;
        }

        setKeys(keyResult.status === "fulfilled" ? keyResult.value : []);
        setTables(tableResult.status === "fulfilled" ? tableResult.value : []);
        setErrors((current) => ({
          ...current,
          keys: keyResult.status === "rejected" ? errorText(keyResult.reason) : null,
          tables: tableResult.status === "rejected" ? errorText(tableResult.reason) : null
        }));
      } finally {
        if (generation === resourceGeneration.current) {
          setLoading((current) => ({ ...current, keys: false, tables: false }));
        }
      }
    },
    [client, signOut]
  );

  useEffect(() => {
    if (!token) {
      return;
    }
    let active = true;
    client.setToken(token);
    setAuthBusy(true);
    void refreshProjects().finally(() => {
      if (active) {
        setAuthBusy(false);
      }
    });
    return () => {
      active = false;
    };
  }, [token, refreshProjects, client]);

  useEffect(() => {
    if (token) {
      void refreshResources(selectedProjectId);
    }
  }, [selectedProjectId, token, refreshResources]);

  const handleLogin = useCallback(
    (username: string, password: string) => {
      setSessionError(null);
      setAuthBusy(true);
      void (async () => {
        try {
          const session = await client.login(username, password);
          client.setToken(session.token);
          writeSessionToken(session.token);
          setToken(session.token);
        } catch (error) {
          client.setToken(null);
          clearSessionToken();
          setSessionError(errorText(error));
          setAuthBusy(false);
        }
      })();
    },
    [client]
  );

  const handleChanged = useCallback(async () => {
    const before = selectedProjectRef.current;
    await refreshProjects();
    if (selectedProjectRef.current === before) {
      await refreshResources(before);
    }
  }, [refreshProjects, refreshResources]);

  const openProject = useCallback((id: string) => {
    selectProject(id);
    setSection("overview");
  }, [selectProject]);

  const backToProjects = useCallback(() => {
    setSection("projects");
  }, []);

  const handleRetry = useCallback(
    async (id: string) => {
      try {
        await client.retryProvisioning(id);
        await handleChanged();
      } catch (error) {
        setErrors((current) => ({ ...current, projects: errorText(error) }));
      }
    },
    [client, handleChanged]
  );

  const handleDeleteConfirm = useCallback(async () => {
    if (!projectToDelete) {
      return;
    }
    setDeleteBusy(true);
    try {
      await client.deleteProject(projectToDelete.id, deleteSlug);
      if (selectedProjectRef.current === projectToDelete.id) {
        selectProject(null);
      }
      setSection("projects");
      setProjectToDelete(null);
      setDeleteSlug("");
      await handleChanged();
    } catch (error) {
      setErrors((current) => ({ ...current, projects: errorText(error) }));
    } finally {
      setDeleteBusy(false);
    }
  }, [client, deleteSlug, handleChanged, projectToDelete, selectProject]);

  const selectedProject = projects.find((project) => project.id === selectedProjectId) ?? null;
  const tabsWithCount = projectTabs.map((tab) => tab.id === "tables" ? { ...tab, count: tables.length } : tab);

  if (!token) {
    return <LoginGate error={sessionError} busy={authBusy} onLogin={handleLogin} />;
  }

  if (authBusy && projects.length === 0) {
    return (
      <main className="gate">
        <div className="gate__sheet">
          <p className="loading" role="status">Verificando la sesión…</p>
        </div>
      </main>
    );
  }

  return (
    <div className="app" data-section={section}>
      <header className="topbar">
        <div className="topbar__brand">
          <span className="topbar__mark" aria-hidden="true" />
          <span>DomiBase</span>
        </div>
        <nav className="topbar__crumbs" aria-label="Ubicación actual">
          <button type="button" className="topbar__link" onClick={backToProjects}>Proyectos</button>
          <span className="topbar__sep" aria-hidden="true">/</span>
          <span>{selectedProject ? selectedProject.slug : "—"}</span>
          {section !== "projects" ? (
            <>
              <span className="topbar__sep" aria-hidden="true">/</span>
              <strong>{sectionTitles[section]}</strong>
            </>
          ) : null}
        </nav>
        <div className="topbar__actions">
          <button type="button" className="button button--ghost button--small" onClick={() => setSignOutConfirm(true)}>
            Cerrar sesión
          </button>
        </div>
      </header>

      <div className="app__body">
        {section === "projects" ? (
          <main className="desk-main">
            <div className="leaf leaf--enter">
              <ProjectsSection
                client={client}
                projects={projects}
                loading={loading.projects}
                error={errors.projects}
                selectedProjectId={selectedProjectId}
                onSelectProject={selectProject}
                onOpenProject={openProject}
                onRequestDelete={(project) => {
                  setProjectToDelete(project);
                  setDeleteSlug("");
                }}
                onChanged={() => void handleChanged()}
              />
            </div>
          </main>
        ) : (
          <>
            <aside className="binding">
              <TabRail
                items={tabsWithCount}
                active={section as ProjectSectionId}
                onSelect={(id) => setSection(id)}
                onBack={backToProjects}
                projectSlug={selectedProject?.slug ?? null}
              />
              <div className="binding__foot">
                <p className="session-state">Sesión activa · válida solo en esta pestaña</p>
              </div>
            </aside>

            <main className="desk-main">
              <div
                role="tabpanel"
                id="panel-overview"
                aria-labelledby="tab-overview"
                hidden={section !== "overview"}
              >
                <div className={`leaf${section === "overview" ? " leaf--enter" : ""}`}>
                  <ProjectOverviewSection
                    project={selectedProject}
                    error={errors.projects}
                    onRetry={(id) => void handleRetry(id)}
                    onDelete={(project) => {
                      setProjectToDelete(project);
                      setDeleteSlug("");
                    }}
                    onOpenTables={() => setSection("tables")}
                    onOpenConnection={() => setSection("connection")}
                  />
                </div>
              </div>

              <div role="tabpanel" id="panel-tables" aria-labelledby="tab-tables" hidden={section !== "tables"}>
                <div className={`leaf${section === "tables" ? " leaf--enter" : ""}`}>
                  <TableEditorSection
                    key={selectedProjectId ?? "none"}
                    client={client}
                    project={selectedProject}
                    tables={tables}
                    loadingTables={loading.tables}
                    tablesError={errors.tables}
                    onTablesChanged={() => void handleChanged()}
                  />
                </div>
              </div>

              <div role="tabpanel" id="panel-connection" aria-labelledby="tab-connection" hidden={section !== "connection"}>
                <div className={`leaf${section === "connection" ? " leaf--enter" : ""}`}>
                  <ConnectionSection
                    key={selectedProjectId ?? "none"}
                    client={client}
                    project={selectedProject}
                    keys={keys}
                    onKeysChanged={() => void handleChanged()}
                  />
                </div>
              </div>
            </main>
          </>
        )}
      </div>

      <ConfirmDialog
        open={signOutConfirm}
        title="Cerrar sesión"
        confirmLabel="Cerrar sesión"
        onClose={() => setSignOutConfirm(false)}
        onConfirm={() => signOut()}
      >
        <p>La sesión se eliminará de esta pestaña. Necesitarás volver a introducir tus credenciales.</p>
      </ConfirmDialog>

      <ConfirmDialog
        open={projectToDelete !== null}
        title="Eliminar proyecto"
        confirmLabel="Eliminar definitivamente"
        busy={deleteBusy}
        confirmDisabled={deleteSlug !== projectToDelete?.slug}
        onClose={() => {
          setProjectToDelete(null);
          setDeleteSlug("");
        }}
        onConfirm={() => void handleDeleteConfirm()}
      >
        <p>
          Se eliminarán los metadatos y la base de datos de <span className="mono">{projectToDelete?.slug}</span>.
          Esta acción no se puede deshacer.
        </p>
        <div className="field">
          <label htmlFor="delete-project-slug">Escribe el slug para confirmar</label>
          <input
            id="delete-project-slug"
            value={deleteSlug}
            onChange={(event) => setDeleteSlug(event.target.value)}
            spellCheck={false}
          />
        </div>
      </ConfirmDialog>
    </div>
  );
}
