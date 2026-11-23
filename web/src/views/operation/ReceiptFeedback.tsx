import { useState } from "react";
import { usePresentation } from "../../presentation/context";
import { Button } from "../../ui/Button";
import { Dialog } from "../../ui/Dialog";
import { StatusBanner } from "../../ui/StatusBanner";
import { ObservedReceipt } from "./ObservedReceipt";

export const ReceiptFeedback = ({
  operationId,
  dismiss,
}: {
  operationId: string;
  dismiss: () => void;
}) => {
  const p = usePresentation();
  const [open, setOpen] = useState(false);
  const title = p.text("ui.viewReceipt");
  return (
    <>
      <StatusBanner
        tone="success"
        dismissLabel={p.text("ui.close")}
        dismiss={dismiss}
        action={
          <Button
            variant="quiet"
            compact
            onPress={() => {
              setOpen(true);
            }}
          >
            {title}
          </Button>
        }
      >
        {p.text("label.recordedChange")}
      </StatusBanner>
      <Dialog
        title={title}
        closeLabel={p.text("ui.close")}
        isOpen={open}
        isDismissable
        onOpenChange={setOpen}
      >
        {open ? <ObservedReceipt operationId={operationId} /> : null}
      </Dialog>
    </>
  );
};
