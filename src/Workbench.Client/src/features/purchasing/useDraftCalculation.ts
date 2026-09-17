import { useEffect, useState } from 'react';
import { calculateDraft, DraftError, type DraftContent, type DraftCalculation } from '../../api/purchaseOrders';
import { ApiError } from '../../api/auth';

type Preview = { draft: DraftContent; result?: DraftCalculation; message?: string; errors?: Record<string, string[]> };
export function useDraftCalculation(draft: DraftContent, enabled: boolean, onAuthLost: () => void) {
  const [preview, setPreview] = useState<Preview>();
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    if (!enabled || (!draft.entries.length && !draft.charges.length && !draft.orderDiscount)) return;
    const controller = new AbortController();
    let current = true;
    const timer = window.setTimeout(() => {
      void calculateDraft(draft, controller.signal).then(result => {
        if (current) setPreview({ draft, result });
      }, error => {
        if (!current) return;
        if (error instanceof ApiError && (error.status === 401 || error.status === 403)) { onAuthLost(); return; }
        setPreview({ draft, message: error instanceof DraftError && error.status === 400 ? 'Review the draft fields to calculate an estimate.' : 'The estimate could not be calculated. Your changes are kept.', errors: error instanceof DraftError ? error.errors : undefined });
      });
    }, 300);
    return () => { current = false; window.clearTimeout(timer); controller.abort(); };
  }, [draft, enabled, onAuthLost, retry]);
  const latest = enabled && preview?.draft === draft ? preview : undefined;
  return { result: latest?.result, errors: latest?.errors, message: latest?.message, retry: () => { setPreview(undefined); setRetry(value => value + 1); } };
}
