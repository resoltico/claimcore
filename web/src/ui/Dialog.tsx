import "./dialog.css";
import type { ReactNode, RefObject } from "react";
import { Dialog as AriaDialog } from "react-aria-components/Dialog";
import { Popover } from "react-aria-components/ComboBox";
import { Heading } from "react-aria-components/Heading";
import { Modal, ModalOverlay } from "react-aria-components/Modal";
import { Button } from "./Button";

type Props = {
  title: ReactNode;
  closeLabel: string;
  description?: string;
  children: ReactNode;
  footer?: ReactNode;
  headerEnd?: ReactNode;
  titleIcon?: ReactNode;
  identity?: ReactNode;
  isOpen: boolean;
  isDismissable: boolean;
  onOpenChange: (open: boolean) => void;
  placement?: "center" | "bottom" | "end";
  className?: string;
  anchorRef?: RefObject<HTMLElement | null>;
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
  titleIcon,
  identity,
  isOpen,
  isDismissable,
  onOpenChange,
  placement = "center",
  className = "",
  anchorRef,
}: Props) => {
  const contents = (
    <AriaDialog>
      {({ close }) => (
        <>
          <header className="ui-dialog-header">
            <div className="ui-dialog-heading">
              <div className="ui-dialog-title-group">
                {titleIcon}
                <Heading slot="title" className="ui-dialog-title">
                  <bdi>{title}</bdi>
                </Heading>
              </div>
              {headerEnd}
            </div>
            {identity}
            {description === undefined || anchorRef !== undefined ? null : <p>{description}</p>}
          </header>
          <div className="ui-dialog-body">
            {anchorRef !== undefined && description !== undefined ? <p>{description}</p> : null}
            {children}
          </div>
          <DialogFooter dismissable={isDismissable} label={closeLabel} close={close}>
            {footer}
          </DialogFooter>
        </>
      )}
    </AriaDialog>
  );
  if (anchorRef !== undefined) {
    return (
      <Popover
        triggerRef={anchorRef}
        isOpen={isOpen}
        isKeyboardDismissDisabled={!isDismissable}
        onOpenChange={(next) => {
          if (!next && isDismissable) {
            onOpenChange(false);
          }
        }}
        shouldCloseOnInteractOutside={() => isDismissable}
        placement="top start"
        className={["ui-dialog", "ui-completion-popover", className].filter(Boolean).join(" ")}
      >
        {contents}
      </Popover>
    );
  }
  return (
    <ModalOverlay
      className={`ui-dialog-overlay ui-dialog-overlay--${placement}`}
      isOpen={isOpen}
      isDismissable={isDismissable}
      isKeyboardDismissDisabled={!isDismissable}
      onOpenChange={onOpenChange}
    >
      <Modal className={["ui-dialog", className].filter(Boolean).join(" ")}>{contents}</Modal>
    </ModalOverlay>
  );
};
