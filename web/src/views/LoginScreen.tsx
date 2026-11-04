import { useState } from "react";
import { Modal, ModalOverlay } from "react-aria-components/Modal";
import { Dialog } from "react-aria-components/Dialog";
import { Heading } from "react-aria-components/Heading";
import { SettingsPanel } from "../features/settings/SettingsPanel";
import { rememberLoginReturn } from "../routing/loginReturn";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import { productMetadata } from "../protocol/productMetadata";
import type { Notice } from "../protocol/notices";
export const LoginScreen = ({ message }: { message: Notice | null }) => {
  const p = usePresentation();
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
        <button
          type="button"
          onClick={() => {
            setSettings(true);
          }}
        >
          {p.text("ui.languageAppearance")}
        </button>
      </footer>
      <ModalOverlay
        className="modal-overlay"
        isOpen={settings}
        isDismissable
        onOpenChange={setSettings}
      >
        <Modal className="review-modal">
          <Dialog aria-label={p.text("ui.settings")}>
            <Heading slot="title">{p.text("ui.settings")}</Heading>
            <SettingsPanel />
            <button
              type="button"
              onClick={() => {
                setSettings(false);
              }}
            >
              {p.text("ui.close")}
            </button>
          </Dialog>
        </Modal>
      </ModalOverlay>
    </main>
  );
};
