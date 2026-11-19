import type { ReactNode } from "react";

export type SectionId = "projects" | "overview" | "tables" | "connection";
export type ProjectSectionId = Exclude<SectionId, "projects">;

export interface TabItem {
  readonly id: ProjectSectionId;
  readonly label: string;
  readonly count: number;
}

interface TabRailProps {
  readonly items: readonly TabItem[];
  readonly active: ProjectSectionId;
  readonly onSelect: (id: ProjectSectionId) => void;
  readonly onBack: () => void;
  readonly projectSlug: string | null;
  readonly children?: ReactNode;
}

/** Project-level navigation. Global navigation lives in the top bar. */
export function TabRail({ items, active, onSelect, onBack, projectSlug, children }: TabRailProps) {
  return (
    <>
      <div className="binding__head">
        <p className="binding__eyebrow">Proyecto</p>
        <p className="binding__slug">{projectSlug ?? "Sin proyecto"}</p>
      </div>

      <nav className="tabs" aria-label="Navegación del proyecto">
        {items.map((item) => (
          <button
            key={item.id}
            type="button"
            id={`tab-${item.id}`}
            className={`tab${active === item.id ? " tab--active" : ""}`}
            aria-current={active === item.id ? "page" : undefined}
            onClick={() => onSelect(item.id)}
          >
            <span className="tab__label">{item.label}</span>
            {item.count > 0 ? <span className="tab__count">{item.count}</span> : null}
          </button>
        ))}
      </nav>

      {children}

      <div className="binding__foot">
        <button type="button" className="button button--ghost button--small" onClick={onBack}>
          Volver a proyectos
        </button>
      </div>
    </>
  );
}
