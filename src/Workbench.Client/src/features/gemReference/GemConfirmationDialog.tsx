import { useEffect, useRef, type ReactNode } from 'react';
export function GemConfirmationDialog({ title, children, cancel }: { title: string; children: ReactNode; cancel: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  useEffect(() => { const previous = document.activeElement as HTMLElement | null; dialog.current?.showModal(); return () => previous?.focus(); }, []);
  return <dialog ref={dialog} className="reference-confirmation" aria-label={title} onCancel={event => { event.preventDefault(); cancel(); }}><h2>{title}</h2>{children}</dialog>;
}
