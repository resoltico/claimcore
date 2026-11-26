import { ProductBrand } from "./ProductBrand";
import { useDocumentTitle } from "../hooks/useDocumentTitle";
import { PageTitle } from "../ui/Page";
import { useEffect, useRef, useState } from "react";
import { AboutPanel } from "../features/settings/AboutPanel";
import { recordAutomaticSignIn, resetSignIn, signedOut } from "../routing/signInAttempt";
import { Dialog } from "../ui/Dialog";
import { Button } from "../ui/Button";
import { presentationLanguageOptions, presentationPseudolanguage } from "@contracts/client";
import { SettingsPanel } from "../features/settings/SettingsPanel";
import { rememberLoginReturn } from "../routing/loginReturn";
import { loginPath } from "../routing/loginPath";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import { productMetadata } from "../protocol/productMetadata";
import type { Notice } from "../protocol/notices";
export const LoginScreen = ({ message }: { message: Notice | null }) => {
  const p = usePresentation();
  useDocumentTitle(p.text("ui.about"));
  const currentLanguage =
    p.language === presentationPseudolanguage
      ? p.text("ui.pseudo")
      : (presentationLanguageOptions.find((option) => option.value === p.language)?.label ??
        p.language);
  const [settings, setSettings] = useState(false);
  const [about, setAbout] = useState(false);
  const [loopStopped, setLoopStopped] = useState(false);
  const redirecting = useRef(false);
  const hasSignedOut = signedOut(window.sessionStorage);

  useEffect(() => {
    if (message !== null || hasSignedOut || redirecting.current) {
      return;
    }
    if (!recordAutomaticSignIn(window.sessionStorage, Date.now())) {
      queueMicrotask(() => {
        setLoopStopped(true);
      });
      return;
    }
    redirecting.current = true;
    rememberLoginReturn();
    window.location.replace(loginPath(p.language));
  }, [message, hasSignedOut, p.language]);
  if (!hasSignedOut && message === null && !loopStopped) {
    return (
      <main className="loading" role="status">
        {p.text("ui.loadingSession")}
      </main>
    );
  }
  return (
    <main className="login-shell">
      <section aria-labelledby="login-title" className="login-card">
        <p className="product-name">
          <ProductBrand />
        </p>
        <PageTitle
          id="login-title"
          title={p.text(hasSignedOut ? "notice.signedOutTitle" : "ui.signInTitle")}
        />
        {loopStopped ? <p role="alert">{p.text("notice.signInIncomplete")}</p> : null}
        {message === null ? null : (
          <p className="error" role="alert">
            <NoticeView value={message} />
          </p>
        )}
        <a
          className="login-link"
          href={loginPath(p.language)}
          onClick={() => {
            resetSignIn(window.sessionStorage);
            rememberLoginReturn();
          }}
        >
          {p.text(hasSignedOut ? "label.signInAgain" : "ui.signIn")}
        </a>
      </section>
      <footer className="login-footer">
        <span>{productMetadata.productVersion}</span>
        <Button
          variant="quiet"
          onPress={() => {
            setAbout(true);
          }}
        >
          {p.text("ui.about")}
        </Button>
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
        title={p.text("ui.about")}
        closeLabel={p.text("ui.close")}
        isOpen={about}
        isDismissable
        onOpenChange={setAbout}
      >
        <AboutPanel />
      </Dialog>
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
