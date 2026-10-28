import { useState } from "react";
import { Modal, ModalOverlay } from "react-aria-components/Modal";
import { Dialog } from "react-aria-components/Dialog";
import { Heading } from "react-aria-components/Heading";
import { usePresentation } from "../presentation/context";
import { PresentationControls } from "../presentation/PresentationControls";
import { useSessionContext } from "../session/SessionContext";
import { useCompact } from "../hooks/useCompact";
import { productMetadata } from "../protocol/productMetadata";
import { navigate, guardedDeparture } from "../routing/navigation";
const Navigation = ({
  active,
  views,
  locked,
}: {
  active: string;
  views: ReadonlyArray<string>;
  locked: boolean;
}) => {
  const p = usePresentation();
  const labels = {
    cases: "ui.cases",
    operations: "ui.operations",
    authority: "subject.AUTHORITY.label",
  } as const;
  return (
    <nav aria-label={p.text("ui.primary")}>
      {(["cases", "operations", "authority"] as const)
        .filter((name) => views.includes(name.toUpperCase()))
        .map((name) => (
          <a
            key={name}
            href={`/${name}`}
            aria-current={active === name ? "page" : undefined}
            aria-disabled={locked || undefined}
            onClick={(event) => {
              event.preventDefault();
              navigate(`/${name}`);
            }}
          >
            {p.text(labels[name])}
          </a>
        ))}
    </nav>
  );
};
const Settings = ({ open, close }: { open: boolean; close: () => void }) => {
  const p = usePresentation();
  return (
    <ModalOverlay
      className="modal-overlay"
      isOpen={open}
      isDismissable
      onOpenChange={(next) => {
        if (!next) {
          close();
        }
      }}
    >
      <Modal className="review-modal">
        <Dialog aria-label={p.text("ui.settings")}>
          <Heading slot="title">{p.text("ui.settings")}</Heading>
          <PresentationControls />
          <section>
            <h3>{p.text("ui.about")}</h3>
            <details>
              <summary>{p.text("ui.technicalDetails")}</summary>
              <p>{p.text("ui.productVersion", { version: productMetadata.productVersion })}</p>
              <p>{p.text("ui.webIdentity", { digest: productMetadata.contractIdentity })}</p>
            </details>
          </section>
          <button type="button" onClick={close}>
            {p.text("ui.backToCases")}
          </button>
        </Dialog>
      </Modal>
    </ModalOverlay>
  );
};
const HeaderTools = ({ locked, settings }: { locked: boolean; settings: () => void }) => {
  const p = usePresentation();
  const { logout } = useSessionContext();
  return (
    <>
      <button type="button" data-settings-button onClick={settings}>
        {p.text("ui.settings")}
      </button>
      <button
        type="button"
        disabled={locked}
        onClick={() => {
          guardedDeparture(() => {
            void logout();
          });
        }}
      >
        {p.text("ui.signOut")}
      </button>
    </>
  );
};
const HeaderNavigation = ({
  compact,
  active,
  views,
  locked,
  settings,
}: {
  compact: boolean;
  active: string;
  views: ReadonlyArray<string>;
  locked: boolean;
  settings: () => void;
}) => {
  const p = usePresentation();
  const nav = <Navigation active={active} views={views} locked={locked} />;
  const tools = <HeaderTools locked={locked} settings={settings} />;
  return compact ? (
    <details className="compact-menu">
      <summary>{p.text("ui.menu")}</summary>
      {nav}
      {tools}
    </details>
  ) : (
    <>
      {nav}
      {tools}
    </>
  );
};
export const WorkspaceHeader = ({
  active,
  views,
  locked,
}: {
  active: string;
  views: ReadonlyArray<string>;
  locked: boolean;
}) => {
  const compact = useCompact();
  const [settings, setSettings] = useState(false);
  return (
    <>
      <header className="workspace-header">
        <a
          className="product-name"
          href="/cases"
          aria-disabled={locked || undefined}
          onClick={(event) => {
            event.preventDefault();
            navigate("/cases");
          }}
        >
          ClaimCore
        </a>
        <HeaderNavigation
          compact={compact}
          active={active}
          views={views}
          locked={locked}
          settings={() => {
            setSettings(true);
          }}
        />
      </header>
      <Settings
        open={settings}
        close={() => {
          setSettings(false);
        }}
      />
    </>
  );
};
