import type { ReactNode } from "react";

interface NoticeProps {
  readonly tone?: "error" | "info";
  readonly title: string;
  readonly children?: ReactNode;
}

export function Notice({ tone = "info", title, children }: NoticeProps) {
  return (
    <div className={`notice notice--${tone}`} role={tone === "error" ? "alert" : undefined}>
      <strong className="notice__title">{title}</strong>
      {children ? <div className="notice__body">{children}</div> : null}
    </div>
  );
}
