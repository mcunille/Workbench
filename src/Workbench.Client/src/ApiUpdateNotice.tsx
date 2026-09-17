import { useSyncExternalStore } from 'react';
import { apiContract } from './api/contract';

export function ApiUpdateNotice({ contract = apiContract }: { contract?: typeof apiContract }) {
  const reloadRequired = useSyncExternalStore(contract.subscribe, contract.getSnapshot);
  if (!reloadRequired) return null;
  return <aside className="api-update-notice" role="alert">
    <strong>Workbench has been updated. Reload required.</strong>
    <p>Saving is paused. Your unsaved changes are still on this page. Copy them before reloading.</p>
    <p>If a save is awaiting confirmation, keep this page open and check the saved record in a new tab before creating another save.</p>
  </aside>;
}
