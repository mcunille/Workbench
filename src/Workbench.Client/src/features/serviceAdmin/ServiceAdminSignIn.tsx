import { useState, type FormEvent } from 'react';
import { FloatingField } from '../../FloatingField';
import { SignInBrand } from '../../SignInBrand';

export function ServiceAdminSignIn({ signIn }: { signIn: (email: string, password: string) => Promise<void> }) {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [pending, setPending] = useState(false);
  const [failed, setFailed] = useState(false);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (pending) return;
    setPending(true);
    setFailed(false);
    try {
      await signIn(email, password);
      setPassword('');
    } catch {
      setFailed(true);
    } finally {
      setPending(false);
    }
  }

  return (
    <section className="auth-card sign-in-card" aria-labelledby="service-admin-sign-in-title">
      <SignInBrand />
      <h1 id="service-admin-sign-in-title">Service-admin sign in</h1>
      <p>Maintain the shared gem reference library with your service-admin account.</p>
      <form className="form-stack" onSubmit={(event) => void submit(event)}>
        <FloatingField label="Email" htmlFor="service-admin-email">
          <input id="service-admin-email" name="email" type="email" placeholder=" " autoComplete="username"
            value={email} onChange={(event) => setEmail(event.target.value)} required />
        </FloatingField>
        <FloatingField label="Password" htmlFor="service-admin-password">
          <input id="service-admin-password" name="password" type="password" placeholder=" " autoComplete="current-password"
            value={password} onChange={(event) => setPassword(event.target.value)} required />
        </FloatingField>
        {failed ? <p className="form-message error" role="alert">We could not sign you in. Check your service-admin credentials and try again.</p> : null}
        <button className="primary" type="submit" disabled={pending}>{pending ? 'Signing in…' : 'Sign in'}</button>
      </form>
      <a className="text-link" href="/">Tenant sign in</a>
    </section>
  );
}
