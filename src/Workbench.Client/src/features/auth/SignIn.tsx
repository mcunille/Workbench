import { FloatingField } from '../../FloatingField';
import { useState, type FormEvent } from 'react';
import { useAuth } from './useAuth';

export function SignIn() {
  const { identity, signIn } = useAuth();
  const [pending, setPending] = useState(false);
  const [failed, setFailed] = useState(false);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setPending(true);
    setFailed(false);
    const form = new FormData(event.currentTarget);
    try {
      await signIn(
        String(form.get('email') ?? ''),
        String(form.get('password') ?? ''),
      );
    } catch {
      setFailed(true);
    } finally {
      setPending(false);
    }
  }

  if (identity) {
    return <p role="status">Signed in</p>;
  }

  return (
    <section className="auth-card sign-in-card" aria-labelledby="sign-in-title">
      <div className="sign-in-brand">
        <img src="/stag-mark.svg" width="112" height="150" alt="" />
        <p className="sign-in-wordmark">Workbench</p>
        <p className="sign-in-byline">by The White Stag Collection</p>
      </div>
      <h1 id="sign-in-title">Sign in</h1>
      <p className="lede">
        Use the Workbench account assigned to your organization.
      </p>
      <form className="form-stack" onSubmit={(event) => void submit(event)}>
        <FloatingField label="Email">
          <input placeholder=" " name="email" type="email" autoComplete="username" required />
        </FloatingField>
        <FloatingField label="Password">
          <input placeholder=" "
            name="password"
            type="password"
            autoComplete="current-password"
            required
          />
        </FloatingField>
        {failed ? (
          <p className="form-message error" role="alert">
            The email or password was not accepted.
          </p>
        ) : null}
        <button className="primary" type="submit" disabled={pending}>
          {pending ? 'Signing in…' : 'Sign in'}
        </button>
      </form>
      <a className="text-link" href="/recover">
        Forgot your password?
      </a>
    </section>
  );
}
