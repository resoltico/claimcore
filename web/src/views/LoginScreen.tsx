import { useState } from "react";
import { Dialog } from "../ui/Dialog";
import { Button } from "../ui/Button";
import { presentationLanguageOptions, presentationPseudolanguage } from "@contracts/client";
import { SettingsPanel } from "../features/settings/SettingsPanel";
import { rememberLoginReturn } from "../routing/loginReturn";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import { productMetadata } from "../protocol/productMetadata";
import type { Notice } from "../protocol/notices";
export const LoginScreen = ({ message }: { message: Notice | null }) => {
  const p = usePresentation();
  const currentLanguage =
    p.language === presentationPseudolanguage
      ? p.text("ui.pseudo")
      : (presentationLanguageOptions.find((option) => option.value === p.language)?.label ??
        p.language);
  const [settings, setSettings] = useState(false);
  return (
    <main className="login-shell">
      <section aria-labelledby="login-title" className="login-card">
        <p className="product-name">
          <span className="product-mark" aria-hidden="true">
            C
          </span>{" "}
          ClaimCore
        </p>
        <h1 id="login-title">{p.text("ui.signInTitle")}</h1>
        <p>{p.text("ui.loginInstructions")}</p>
        {message === null ? null : (
          <p className="error" role="alert">
            <NoticeView value={message} />
          </p>
        )}
        <a className="login-link" href="/auth/login" onClick={rememberLoginReturn}>
          {p.text("ui.signIn")}
        </a>
      </section>
      <footer className="login-footer">
        <span>{productMetadata.productVersion}</span>
        <Button
          variant="secondary"
          onPress={() => {
            setSettings(true);
          }}
        >
          {p.text("ui.languageAppearance")} · <bdi lang={p.language}>{currentLanguage}</bdi>
        </Button>
      </footer>
      <Dialog
        title={p.text("ui.settings")}
        closeLabel={p.text("ui.close")}
        isOpen={settings}
        isDismissable
        onOpenChange={setSettings}
      >
        <SettingsPanel />
      </Dialog>
    </main>
  );
};
