import { useEffect, useState, type FormEvent } from 'react';
import {
  disableTenantUser,
  getTenantUsers,
  initiateTenantUserRecovery,
  inviteTenantUser,
  reactivateTenantUser,
  revokeTenantUserSessions,
  type TenantUser,
} from '../../api/auth';

const accountState = {
  enabled: 1,
  disabled: 2,
  invited: 3,
} as const;

function stateLabel(state: number): string {
  if (state === accountState.enabled) return 'Enabled';
  if (state === accountState.disabled) return 'Disabled';
  if (state === accountState.invited) return 'Invited';
  return 'Unknown';
}

export function TenantUsers() {
  const [users, setUsers] = useState<TenantUser[]>();
  const [message, setMessage] = useState<string>();

  async function load() {
    try {
      setUsers(await getTenantUsers());
    } catch {
      setMessage('Tenant users could not be loaded.');
    }
  }

  useEffect(() => {
    let current = true;
    void getTenantUsers().then(
      (result) => {
        if (current) setUsers(result);
      },
      () => {
        if (current) setMessage('Tenant users could not be loaded.');
      },
    );
    return () => {
      current = false;
    };
  }, []);

  async function invite(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    try {
      await inviteTenantUser(String(data.get('email') ?? ''));
      form.reset();
      setMessage('Invitation queued.');
      await load();
    } catch {
      setMessage('The invitation could not be queued.');
    }
  }

  async function setUserState(user: TenantUser, enabled: boolean) {
    try {
      await (enabled ? reactivateTenantUser(user.id) : disableTenantUser(user.id));
      setMessage(enabled ? 'User reactivated.' : 'User disabled.');
      await load();
    } catch {
      setMessage('The user could not be updated.');
    }
  }

  async function recover(user: TenantUser) {
    try {
      await initiateTenantUserRecovery(user.id);
      setMessage('Recovery instructions queued.');
    } catch {
      setMessage('Recovery could not be initiated.');
    }
  }

  async function revokeSessions(user: TenantUser) {
    try {
      await revokeTenantUserSessions(user.id);
      setMessage('User sessions revoked.');
    } catch {
      setMessage('User sessions could not be revoked.');
    }
  }

  return (
    <section className="panel" aria-labelledby="tenant-users-title">
      <div className="panel-heading">
        <div>
          <p className="eyebrow">Administration</p>
          <h2 id="tenant-users-title">Tenant users</h2>
        </div>
      </div>
      <form className="inline-form" onSubmit={(event) => void invite(event)}>
        <label>
          Invite email
          <input name="email" type="email" autoComplete="email" required />
        </label>
        <button className="primary" type="submit">Send invitation</button>
      </form>
      {users ? (
        <ul className="record-list">
          {users.map((user) => (
            <li key={user.id}>
              <span>
                <strong>{user.email ?? 'Pending account'}</strong>
                <small>{stateLabel(user.state)}</small>
              </span>
              <span className="button-row">
                <button className="secondary" type="button" onClick={() => void recover(user)}>
                  Recovery
                </button>
                <button className="secondary" type="button" onClick={() => void revokeSessions(user)}>
                  Revoke sessions
                </button>
                {user.state === accountState.disabled ? (
                  <button className="secondary" type="button" onClick={() => void setUserState(user, true)}>
                    Reactivate
                  </button>
                ) : (
                  <button className="secondary danger" type="button" onClick={() => void setUserState(user, false)}>
                    Disable
                  </button>
                )}
              </span>
            </li>
          ))}
        </ul>
      ) : (
        <p role="status">Loading tenant users…</p>
      )}
      {message ? <p className="form-message" role="status">{message}</p> : null}
    </section>
  );
}
