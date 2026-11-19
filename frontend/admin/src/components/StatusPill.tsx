interface StatusPillProps {
  readonly status: string;
}

export function StatusPill({ status }: StatusPillProps) {
  const normalized = status.toLowerCase();
  const variant = normalized.includes("fail")
    ? "failed"
    : normalized.includes("ready")
      ? "ready"
      : normalized.includes("delet")
        ? "deleting"
        : "provisioning";

  return (
    <span className={`status status--${variant}`}>
      <span className="status__mark" aria-hidden="true" />
      {status}
    </span>
  );
}
