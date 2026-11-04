import { useState } from "react";
import { Modal, ModalOverlay } from "react-aria-components/Modal";
import { Dialog } from "react-aria-components/Dialog";
import { Heading } from "react-aria-components/Heading";
import { usePresentation } from "../presentation/context";
import { useSessionContext } from "../session/SessionContext";
import { useCompact } from "../hooks/useCompact";
import { navigate, guardedDeparture } from "../routing/navigation";
const Navigation = ({
  active,
  views,
  locked,
  close,
}: {
  active: string;
  views: ReadonlyArray<string>;
  locked: boolean;
  close?: () => void;
}) => {
  const p = usePresentation();
  const labels = {
    cases: "ui.cases",
    work: "ui.reminders",
    operations: "ui.operations",
    authority: "ui.access",
  } as const;
  return (
    <nav aria-label={p.text("ui.primary")}>
      {(["cases", "work", "operations", "authority"] as const)
        .filter((name) => views.includes(name === "work" ? "CASES" : name.toUpperCase()))
        .map((name) => (
          <a
            key={name}
            href={`/${name}`}
            aria-current={active === name ? "page" : undefined}
            aria-disabled={locked || undefined}
            onClick={(event) => {
              event.preventDefault();
              if (navigate(`/${name}`)) {
                close?.();
              }
            }}
          >
            {p.text(labels[name])}
          </a>
        ))}
    </nav>
  );
};
const AccountMenu = ({
  locked,
  compact,
  close,
}: {
  locked: boolean;
  compact: boolean;
  close?: () => void;
}) => {
  const p = usePresentation();
  const { logout } = useSessionContext();
  return (
    <details className="account-menu">
      <summary>{p.text("ui.account")}</summary>
      <a
        href="/settings"
        aria-disabled={locked || undefined}
        onClick={(event) => {
          event.preventDefault();
          if (navigate("/settings")) {
            event.currentTarget.closest("details")?.removeAttribute("open");
            close?.();
          }
        }}
      >
        {p.text("ui.settings")}
      </a>
      {compact ? null : (
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
      )}
    </details>
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
  const p = usePresentation();
  const compact = useCompact();
  const [open, setOpen] = useState(false);
  const close = () => {
    setOpen(false);
  };
  return (
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
      {compact ? (
        <>
          <button
            type="button"
            aria-expanded={open}
            onClick={() => {
              setOpen(true);
            }}
          >
            {p.text("ui.menu")}
          </button>
          <ModalOverlay
            className="modal-overlay sheet-overlay"
            isOpen={open}
            isDismissable
            onOpenChange={setOpen}
          >
            <Modal className="navigation-sheet">
              <Dialog aria-label={p.text("ui.menu")}>
                <Heading slot="title">{p.text("ui.menu")}</Heading>
                <Navigation active={active} views={views} locked={locked} close={close} />
                <AccountMenu locked={locked} compact close={close} />
                <button type="button" onClick={close}>
                  {p.text("ui.close")}
                </button>
              </Dialog>
            </Modal>
          </ModalOverlay>
        </>
      ) : (
        <>
          <Navigation active={active} views={views} locked={locked} />
          <AccountMenu locked={locked} compact={false} />
        </>
      )}
    </header>
  );
};
