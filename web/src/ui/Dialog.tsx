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
  footer?: ReactNode;
  headerEnd?: ReactNode;
  isOpen: boolean;
  isDismissable: boolean;
  onOpenChange: (open: boolean) => void;
  placement?: "center" | "bottom" | "end";
  className?: string;
};

const DialogFooter = ({
  children,
  dismissable,
  label,
  close,
}: {
  children: ReactNode;
  dismissable: boolean;
  label: string;
  close: () => void;
}) => {
  if (children !== undefined) {
    return <footer className="ui-dialog-actions">{children}</footer>;
  }
  return dismissable ? (
    <footer className="ui-dialog-actions">
      <Button variant="secondary" onPress={close}>
        {label}
      </Button>
    </footer>
  ) : null;
};
/** react-aria owns focus containment and restoration; dismissal remains an explicit caller policy. */
export const Dialog = ({
  title,
  closeLabel,
  description,
  children,
  footer,
  headerEnd,
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
            <header className="ui-dialog-header">
              <div className="ui-dialog-heading">
                <Heading slot="title" className="ui-dialog-title">
                  {title}
                </Heading>
                {headerEnd}
              </div>
              {description === undefined ? null : <p>{description}</p>}
            </header>
            <div className="ui-dialog-body">{children}</div>
            <DialogFooter dismissable={isDismissable} label={closeLabel} close={close}>
              {footer}
            </DialogFooter>
          </>
        )}
      </AriaDialog>
    </Modal>
  </ModalOverlay>
);
