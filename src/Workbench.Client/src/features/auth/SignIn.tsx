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
      await signIn(String(form.get('email') ?? ''), String(form.get('password') ?? ''));
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
    <section className="auth-card" aria-labelledby="sign-in-title">
      <p className="eyebrow">Secure tenant access</p>
      <h1 id="sign-in-title">Sign in</h1>
      <p className="lede">Use the Workbench account assigned to your organization.</p>
      <form className="form-stack" onSubmit={(event) => void submit(event)}>
        <label>
          Email
          <input name="email" type="email" autoComplete="username" required />
        </label>
        <label>
          Password
          <input name="password" type="password" autoComplete="current-password" required />
        </label>
        {failed ? (
          <p className="form-message error" role="alert">
            The email or password was not accepted.
          </p>
        ) : null}
        <button className="primary" type="submit" disabled={pending}>
          {pending ? 'Signing in…' : 'Sign in'}
        </button>
      </form>
      <a className="text-link" href="/recover">Forgot your password?</a>
    </section>
  );
}
