import { useEffect, useState } from 'react';
import { getSystem, type SystemInformation } from './api/system';

export function App() {
  const [system, setSystem] = useState<SystemInformation>();
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let isCurrent = true;

    void getSystem().then(
      (result) => {
        if (isCurrent) {
          setSystem(result);
        }
      },
      () => {
        if (isCurrent) {
          setFailed(true);
        }
      },
    );

    return () => {
      isCurrent = false;
    };
  }, []);

  if (failed) {
    return (
      <main className="shell">
        <p role="alert">Workbench is temporarily unavailable.</p>
      </main>
    );
  }

  if (!system) {
    return (
      <main className="shell">
        <p role="status">Loading Workbench…</p>
      </main>
    );
  }

  return (
    <main className="shell">
      <p className="eyebrow">Portable operations workspace</p>
      <h1>{system.name}</h1>
      <p className="version">Version {system.version}</p>
    </main>
  );
}
