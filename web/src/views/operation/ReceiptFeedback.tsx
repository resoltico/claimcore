import type { OperationReceipt } from "../../protocol/types";
import { renderCatalogKey } from "../../presentation/messages";
import { useState } from "react";
import { usePresentation } from "../../presentation/context";
import { Button } from "../../ui/Button";
import { Dialog } from "../../ui/Dialog";
import { StatusBanner } from "../../ui/StatusBanner";
import { ObservedReceipt } from "./ObservedReceipt";

export const receiptOutcomeMessage = ({ screen }: OperationReceipt) =>
  screen.regions.flatMap(({ content }) => (content.kind === "NOTICE" ? content.notices : []))[0]
    ?.messageId;

export const ReceiptFeedback = ({
  operationId,
  messageId,
  dismiss,
}: {
  operationId: string;
  messageId?: string | undefined;
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
        {renderCatalogKey(p, messageId ?? "label.recordedChange")}
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
