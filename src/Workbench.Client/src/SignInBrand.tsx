import { Wordmark } from './Wordmark';

export function SignInBrand() {
  return (
    <div className="sign-in-brand">
      <img src="/stag-mark.svg" width="112" height="150" alt="" />
      <p className="sign-in-wordmark">
        <Wordmark />
      </p>
      <p className="sign-in-byline">by The White Stag Collection</p>
    </div>
  );
}
