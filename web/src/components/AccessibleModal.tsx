import type { ReactNode } from "react";
import { Button } from "react-aria-components/Button";
import { Dialog } from "react-aria-components/Dialog";
import { Heading } from "react-aria-components/Heading";
import { Modal, ModalOverlay } from "react-aria-components/Modal";

type AccessibleModalProps = {
  title: string;
  closeLabel: string;
  description: string;
  children: ReactNode;
  isOpen: boolean;
  isDismissable: boolean;
  onOpenChange: (open: boolean) => void;
};
/** One persistent dialog traps focus and returns it to the trigger. */
export const AccessibleModal = ({
  title,
  closeLabel,
  description,
  children,
  isOpen,
  isDismissable,
  onOpenChange,
}: AccessibleModalProps) => (
  <ModalOverlay
    className="modal-overlay"
    isOpen={isOpen}
    isDismissable={isDismissable}
    isKeyboardDismissDisabled={!isDismissable}
    onOpenChange={onOpenChange}
  >
    <Modal className="review-modal">
      <Dialog aria-label={title}>
        {({ close }) => (
          <>
            <Heading slot="title">{title}</Heading>
            <p>{description}</p>
            {children}
            {isDismissable ? (
              <Button className="secondary-button" onPress={close}>
                {closeLabel}
              </Button>
            ) : null}
          </>
        )}
      </Dialog>
    </Modal>
  </ModalOverlay>
);
