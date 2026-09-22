import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import type { Notice } from "../api/notices";
import { Button } from "react-aria-components/Button";
import { Form } from "react-aria-components/Form";
import { useState } from "react";
import { TextInput } from "../components/TextInput";

type LoginScreenProps = {
  tokenAvailable: boolean;
  message: Notice | null;
  onLogin: (credential: string) => Promise<void>;
};

export const LoginScreen = ({ tokenAvailable, message, onLogin }: LoginScreenProps) => {
  const p = usePresentation();
  const [credential, setCredential] = useState("");
  const [submitting, setSubmitting] = useState(false);

  const submit = async (): Promise<void> => {
    if (!tokenAvailable || submitting) return;
    setSubmitting(true);
    await onLogin(credential);
    setSubmitting(false);
  };

  return (
    <main className="login-shell">
      <Form
        aria-labelledby="login-title"
        className="login-card"
        onSubmit={(event) => {
          event.preventDefault();
          void submit();
        }}
      >
        <p className="eyebrow">{p.text("ui.trustedInstallation")}</p>
        <h1 id="login-title">ClaimCore</h1>
        <p>{p.text("ui.loginInstructions")}</p>
        <TextInput
          id="bootstrap-credential"
          label={p.text("ui.credential")}
          value={credential}
          onChange={setCredential}
          type="password"
        />
        {message === null ? null : (
          <p className="error" role="alert">
            <NoticeView value={message} />
          </p>
        )}
        <Button type="submit" isDisabled={!tokenAvailable || submitting}>
          {submitting ? p.text("ui.signingIn") : p.text("ui.signIn")}
        </Button>
      </Form>
    </main>
  );
};
