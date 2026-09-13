import { Button } from "react-aria-components/Button";
import { Form } from "react-aria-components/Form";
import { useState } from "react";
import { TextInput } from "../components/TextInput";

type LoginScreenProps = {
  tokenAvailable: boolean;
  message: string | null;
  onLogin: (credential: string) => Promise<void>;
};

export const LoginScreen = ({ tokenAvailable, message, onLogin }: LoginScreenProps) => {
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
        <p className="eyebrow">Trusted local installation</p>
        <h1 id="login-title">ClaimCore</h1>
        <p>
          Enter the bootstrap credential from the owner-private file printed when the local Web host
          started.
        </p>
        <TextInput
          id="bootstrap-credential"
          label="Bootstrap credential"
          value={credential}
          onChange={setCredential}
          type="password"
        />
        {message === null ? null : (
          <p className="error" role="alert">
            {message}
          </p>
        )}
        <Button type="submit" isDisabled={!tokenAvailable || submitting}>
          {submitting ? "Signing in…" : "Sign in"}
        </Button>
      </Form>
    </main>
  );
};
