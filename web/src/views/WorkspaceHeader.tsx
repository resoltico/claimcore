import { IdentifierMessage } from "../presentation/IdentifierMessage";
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
  onNavigate,
}: {
  active: string;
  views: ReadonlyArray<string>;
  locked: boolean;
  onNavigate?: () => void;
}) => {
  const p = usePresentation();
  const labels = {
    cases: "ui.cases",
    operations: "ui.operations",
    authority: "ui.access",
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
              if (navigate(`/${name}`)) {
                onNavigate?.();
              }
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
              <p>
                <IdentifierMessage
                  id="ui.webIdentity"
                  values={{ digest: productMetadata.contractIdentity }}
                  argument="digest"
                />
              </p>
            </details>
          </section>
          <button type="button" onClick={close}>
            {p.text("ui.close")}
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
const MenuTrigger = ({ open, show }: { open: boolean; show: () => void }) => {
  const p = usePresentation();
  return (
    <button type="button" aria-expanded={open} onClick={show}>
      {p.text("ui.menu")}
    </button>
  );
};
const CompactNavigation = ({
  active,
  views,
  locked,
  settings,
}: {
  active: string;
  views: ReadonlyArray<string>;
  locked: boolean;
  settings: () => void;
}) => {
  const p = usePresentation();
  const [open, setOpen] = useState(false);
  const close = () => {
    setOpen(false);
  };
  const openSettings = () => {
    close();
    settings();
  };
  return (
    <>
      <MenuTrigger
        open={open}
        show={() => {
          setOpen(true);
        }}
      />
      <ModalOverlay
        className="modal-overlay sheet-overlay"
        isOpen={open}
        isDismissable
        onOpenChange={setOpen}
      >
        <Modal className="navigation-sheet">
          <Dialog aria-label={p.text("ui.menu")}>
            <Heading slot="title">{p.text("ui.menu")}</Heading>
            <Navigation active={active} views={views} locked={locked} onNavigate={close} />
            <HeaderTools locked={locked} settings={openSettings} />
            <button type="button" onClick={close}>
              {p.text("ui.close")}
            </button>
          </Dialog>
        </Modal>
      </ModalOverlay>
    </>
  );
};
type HeaderNavigationProps = {
  compact: boolean;
  active: string;
  views: ReadonlyArray<string>;
  locked: boolean;
  settings: () => void;
};
const HeaderNavigation = ({ compact, ...props }: HeaderNavigationProps) =>
  compact ? (
    <CompactNavigation {...props} />
  ) : (
    <>
      <Navigation active={props.active} views={props.views} locked={props.locked} />
      <HeaderTools locked={props.locked} settings={props.settings} />
    </>
  );
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
          <span className="product-mark" aria-hidden="true">
            C
          </span>{" "}
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
