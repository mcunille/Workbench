import { useState, type FormEvent } from 'react';
import {
  consumeInvitation,
  consumeRecovery,
  requestRecovery,
} from '../../api/auth';

export function Recovery({ invitation = false }: { invitation?: boolean }) {
  const token = new URLSearchParams(window.location.hash.slice(1)).get('token')
    ?? new URLSearchParams(window.location.search).get('token');
  const [pending, setPending] = useState(false);
  const [complete, setComplete] = useState(false);
  const [failed, setFailed] = useState(false);

  if (invitation && !token) {
    return (
      <main className="public-shell">
        <section className="auth-card" aria-labelledby="recovery-title">
          <p className="eyebrow">Workbench account</p>
          <h1 id="recovery-title">Invalid invitation</h1>
          <p className="form-message error" role="alert">
            This invitation link is missing its token.
          </p>
          <a className="text-link" href="/">Return to sign in</a>
        </section>
      </main>
    );
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setPending(true);
    setFailed(false);
    const form = new FormData(event.currentTarget);
    try {
      if (token) {
        const password = String(form.get('password') ?? '');
        await (invitation
          ? consumeInvitation(token, password)
          : consumeRecovery(token, password));
      } else {
        await requestRecovery(String(form.get('email') ?? ''));
      }
      setComplete(true);
    } catch {
      setFailed(true);
    } finally {
      setPending(false);
    }
  }

  const title = invitation ? 'Accept invitation' : token ? 'Reset password' : 'Recover account';
  return (
    <main className="public-shell">
      <section className="auth-card" aria-labelledby="recovery-title">
        <p className="eyebrow">Workbench account</p>
        <h1 id="recovery-title">{title}</h1>
        {complete ? (
          <p role="status">
            {token
              ? 'Your password has been set. You can now sign in.'
              : 'If the account is eligible, recovery instructions have been queued.'}
          </p>
        ) : (
          <form className="form-stack" onSubmit={(event) => void submit(event)}>
            {token ? (
              <label>
                New password
                <input name="password" type="password" autoComplete="new-password" required />
              </label>
            ) : (
              <label>
                Email
                <input name="email" type="email" autoComplete="email" required />
              </label>
            )}
            {failed ? (
              <p className="form-message error" role="alert">
                This request could not be completed. The link may be invalid or expired.
              </p>
            ) : null}
            <button className="primary" type="submit" disabled={pending}>
              {pending ? 'Submitting…' : title}
            </button>
          </form>
        )}
        <a className="text-link" href="/">Return to sign in</a>
      </section>
    </main>
  );
}
