import "./dialog.css";
import { createContext, useId, type ReactNode, type RefObject } from "react";
import { Dialog as AriaDialog } from "react-aria-components/Dialog";
import { Popover } from "react-aria-components/ComboBox";
import { Heading } from "react-aria-components/Heading";
import { Modal, ModalOverlay } from "react-aria-components/Modal";
import { Icon } from "./icons";
import { Button } from "./Button";

export const InsideDialog = createContext(false);

type Props = {
  bodyRef?: (node: HTMLDivElement | null) => void;
  headingId?: string;
  closeInHeader?: boolean;
  headingRef?: (node: HTMLElement | null) => void;
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
  placement?: "center" | "top" | "bottom";
  className?: string;
  overlayClassName?: string;
  scrollableBody?: boolean;
  navigationGuard?: boolean;
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
  bodyRef,
  headingRef,
  headingId,
  closeInHeader = false,
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
  overlayClassName = "",
  navigationGuard,
  anchorRef,
  scrollableBody = anchorRef !== undefined,
}: Props) => {
  const generatedTitleId = useId();
  const titleId = headingId ?? generatedTitleId;
  const contents = (
    <AriaDialog
      aria-labelledby={titleId}
      data-navigation-guard={navigationGuard === true ? "" : undefined}
    >
      {({ close }) => (
        <InsideDialog value>
          <div className="ui-dialog-header">
            <div className="ui-dialog-heading">
              <div className="ui-dialog-title-group">
                {titleIcon}
                <Heading
                  ref={headingRef}
                  tabIndex={headingRef === undefined ? undefined : -1}
                  id={titleId}
                  slot="title"
                  className="ui-dialog-title"
                >
                  <bdi>{title}</bdi>
                </Heading>
              </div>
              {headerEnd}
              {closeInHeader ? (
                <Button aria-label={closeLabel} variant="quiet" compact onPress={close}>
                  <Icon name="close" />
                </Button>
              ) : null}
            </div>
            {identity}
            {description === undefined || anchorRef !== undefined ? null : <p>{description}</p>}
          </div>
          <div
            ref={bodyRef}
            className="ui-dialog-body"
            role={scrollableBody ? "group" : undefined}
            tabIndex={scrollableBody ? 0 : undefined}
            aria-labelledby={scrollableBody ? titleId : undefined}
          >
            {anchorRef !== undefined && description !== undefined ? <p>{description}</p> : null}
            {children}
          </div>
          <DialogFooter
            dismissable={isDismissable && !closeInHeader}
            label={closeLabel}
            close={close}
          >
            {footer}
          </DialogFooter>
        </InsideDialog>
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
      className={`ui-dialog-overlay ui-dialog-overlay--${placement} ${overlayClassName}`}
      isOpen={isOpen}
      isDismissable={isDismissable}
      isKeyboardDismissDisabled={!isDismissable}
      onOpenChange={onOpenChange}
    >
      <Modal className={["ui-dialog", className].filter(Boolean).join(" ")}>{contents}</Modal>
    </ModalOverlay>
  );
};
