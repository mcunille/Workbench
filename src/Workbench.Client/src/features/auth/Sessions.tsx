import { useEffect, useState, type FormEvent } from 'react';
import {
  changePassword,
  getSessions,
  revokeAllSessions,
  revokeSession,
  type Session,
} from '../../api/auth';
import { useAuth } from './useAuth';

function formatDate(value: string): string {
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(new Date(value));
}

export function Sessions() {
  const { refresh } = useAuth();
  const [sessions, setSessions] = useState<Session[]>();
  const [message, setMessage] = useState<string>();

  useEffect(() => {
    let current = true;
    void getSessions().then(
      (result) => {
        if (current) setSessions(result);
      },
      () => {
        if (current) setMessage('Sessions could not be loaded.');
      },
    );
    return () => {
      current = false;
    };
  }, []);

  async function revoke(session: Session) {
    try {
      await revokeSession(session.id);
      if (session.isCurrent) {
        await refresh();
      } else {
        setSessions((current) => current?.filter((item) => item.id !== session.id));
        setMessage('Session revoked.');
      }
    } catch {
      setMessage('The session could not be revoked.');
    }
  }

  async function revokeAll() {
    try {
      await revokeAllSessions();
      await refresh();
    } catch {
      setMessage('Sessions could not be revoked.');
    }
  }

  async function updatePassword(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    try {
      await changePassword(
        String(form.get('currentPassword') ?? ''),
        String(form.get('newPassword') ?? ''),
      );
      await refresh();
    } catch {
      setMessage('The password could not be changed.');
    }
  }

  return (
    <section className="panel" aria-labelledby="sessions-title">
      <div className="panel-heading">
        <div>
          <p className="eyebrow">Security</p>
          <h2 id="sessions-title">Sessions</h2>
        </div>
        <button className="secondary danger" type="button" onClick={() => void revokeAll()}>
          Sign out everywhere
        </button>
      </div>
      {sessions ? (
        <ul className="record-list">
          {sessions.map((session) => (
            <li key={session.id}>
              <span>
                <strong>{session.isCurrent ? 'This session' : 'Active session'}</strong>
                <small>Last used {formatDate(session.lastSeenAtUtc)}</small>
              </span>
              <button className="secondary" type="button" onClick={() => void revoke(session)}>
                Revoke
              </button>
            </li>
          ))}
        </ul>
      ) : (
        <p role="status">Loading sessions…</p>
      )}
      <details>
        <summary>Change password</summary>
        <form className="form-stack compact" onSubmit={(event) => void updatePassword(event)}>
          <label>
            Current password
            <input name="currentPassword" type="password" autoComplete="current-password" required />
          </label>
          <label>
            New password
            <input name="newPassword" type="password" autoComplete="new-password" required />
          </label>
          <button className="primary" type="submit">Change password</button>
        </form>
      </details>
      {message ? <p className="form-message" role="status">{message}</p> : null}
    </section>
  );
}
