import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import type { Notice } from "../api/notices";

type LoginScreenProps = {
  message: Notice | null;
};

export const LoginScreen = ({ message }: LoginScreenProps) => {
  const p = usePresentation();

  return (
    <main className="login-shell">
      <section aria-labelledby="login-title" className="login-card">
        <p className="eyebrow">{p.text("ui.registerLabel")}</p>
        <h1 id="login-title">ClaimCore</h1>
        <p>{p.text("ui.loginInstructions")}</p>
        {message === null ? null : (
          <p className="error" role="alert">
            <NoticeView value={message} />
          </p>
        )}
        <a className="login-link" href="/auth/login">
          {p.text("ui.signIn")}
        </a>
      </section>
    </main>
  );
};
