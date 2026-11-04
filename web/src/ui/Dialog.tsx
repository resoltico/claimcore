import "./dialog.css";
import type { ReactNode } from "react";
import { Dialog as AriaDialog } from "react-aria-components/Dialog";
import { Heading } from "react-aria-components/Heading";
import { Modal, ModalOverlay } from "react-aria-components/Modal";
import { Button } from "./Button";

type Props = {
  title: string;
  closeLabel: string;
  description?: string;
  children: ReactNode;
  isOpen: boolean;
  isDismissable: boolean;
  onOpenChange: (open: boolean) => void;
  placement?: "center" | "bottom";
  className?: string;
};

/** react-aria owns focus containment and restoration; dismissal remains an explicit caller policy. */
export const Dialog = ({
  title,
  closeLabel,
  description,
  children,
  isOpen,
  isDismissable,
  onOpenChange,
  placement = "center",
  className = "",
}: Props) => (
  <ModalOverlay
    className={`ui-dialog-overlay ui-dialog-overlay--${placement}`}
    isOpen={isOpen}
    isDismissable={isDismissable}
    isKeyboardDismissDisabled={!isDismissable}
    onOpenChange={onOpenChange}
  >
    <Modal className={["ui-dialog", className].filter(Boolean).join(" ")}>
      <AriaDialog aria-label={title}>
        {({ close }) => (
          <>
            <Heading slot="title" className="ui-dialog-title">
              {title}
            </Heading>
            {description === undefined ? null : <p>{description}</p>}
            {children}
            {isDismissable ? (
              <footer className="ui-dialog-actions">
                <Button variant="secondary" onPress={close}>
                  {closeLabel}
                </Button>
              </footer>
            ) : null}
          </>
        )}
      </AriaDialog>
    </Modal>
  </ModalOverlay>
);
