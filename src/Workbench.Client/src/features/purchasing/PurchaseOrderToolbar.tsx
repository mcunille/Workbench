import { useEffect, useRef, useState, type ReactNode } from 'react';

/** Shared top-of-page transition for purchase editing, review and ordered records. */
export function PurchaseOrderToolbar({ children, className = '' }: { children: ReactNode; className?: string }) {
  const marker = useRef<HTMLDivElement>(null);
  const [pinned, setPinned] = useState(false);
  useEffect(() => {
    if (!marker.current || typeof IntersectionObserver === 'undefined') return;
    const observer = new IntersectionObserver(([entry]) => {
      setPinned(!entry.isIntersecting && entry.boundingClientRect.top < 0);
    });
    observer.observe(marker.current);
    return () => observer.disconnect();
  }, []);
  return <>
    <div ref={marker} className="po-toolbar-start" aria-hidden="true" />
    <div className={`po-editor-toolbar${pinned ? ' is-pinned' : ''}${className ? ` ${className}` : ''}`}>{children}</div>
  </>;
}
