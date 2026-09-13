import { createPortal } from 'react-dom';
import { useEffect, useRef, type ReactNode } from 'react';
export function SupplierDialog({ title, children, cancel }: { title: string; children: ReactNode; cancel(): void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  useEffect(() => { const previous = document.activeElement as HTMLElement | null; dialog.current?.showModal(); return () => previous?.focus(); }, []);
  return createPortal(<dialog className="po-supplier-dialog" ref={dialog} aria-label={title} onCancel={event => { event.preventDefault(); cancel(); }}>
    <h2>{title}</h2>{children}
  </dialog>, document.body);
}
