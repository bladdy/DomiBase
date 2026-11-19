import { useEffect, useId, useRef, type ReactNode } from "react";

interface ConfirmDialogProps {
  readonly open: boolean;
  readonly title: string;
  readonly children: ReactNode;
  readonly confirmLabel?: string;
  readonly busy?: boolean;
  readonly confirmDisabled?: boolean;
  readonly onConfirm: () => void;
  readonly onClose: () => void;
}

export function ConfirmDialog({
  open,
  title,
  children,
  confirmLabel = "Confirmar",
  busy = false,
  confirmDisabled = false,
  onConfirm,
  onClose
}: ConfirmDialogProps) {
  const dialogRef = useRef<HTMLDialogElement>(null);
  const titleId = useId();

  useEffect(() => {
    const dialog = dialogRef.current;
    if (!dialog) {
      return;
    }
    if (open && !dialog.open) {
      dialog.showModal();
    } else if (!open && dialog.open) {
      dialog.close();
    }
  }, [open]);

  return (
    <dialog
      ref={dialogRef}
      className="confirm"
      aria-labelledby={titleId}
      onCancel={(event) => {
        event.preventDefault();
        if (!busy) {
          onClose();
        }
      }}
      onClick={(event) => {
        if (event.target === dialogRef.current && !busy) {
          onClose();
        }
      }}
    >
      <div className="confirm__sheet">
        <h2 id={titleId} className="confirm__title">
          {title}
        </h2>
        <div className="confirm__body">{children}</div>
        <div className="confirm__actions">
          <button type="button" className="button button--ghost" onClick={onClose} disabled={busy} autoFocus>
            Cancelar
          </button>
          <button
            type="button"
            className="button button--danger"
            onClick={onConfirm}
            disabled={busy || confirmDisabled}
          >
            {busy ? "Trabajando…" : confirmLabel}
          </button>
        </div>
      </div>
    </dialog>
  );
}
