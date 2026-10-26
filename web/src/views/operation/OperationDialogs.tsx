import { usePresentation } from "../../presentation/context";
import { Button } from "react-aria-components/Button";
import { useEffect, useRef } from "react";
import type { Notice } from "../../protocol/notices";
import { NoticeView } from "../../presentation/Message";
import type { ProductMetadata, CaseReceipt } from "../../api/service";
import { ReceiptFields } from "../../components/ReceiptFields";
type AcceptedOperationProps = {
  definition: ProductMetadata;
  receipt: CaseReceipt;
  message: Notice | null;
  onCommitted: (receipt: CaseReceipt) => void;
};
export const AcceptedOperation = ({
  definition,
  receipt,
  message,
  onCommitted,
}: AcceptedOperationProps) => {
  const p = usePresentation();
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    heading.current?.focus();
  }, []);
  return (
    <section aria-labelledby="accepted-title" className="receipt">
      {message === null ? null : (
        <p role="alert">
          <NoticeView value={message} />
        </p>
      )}
      <h2 ref={heading} tabIndex={-1} id="accepted-title">
        {p.text("ui.acceptedOperation")}
      </h2>
      <p>
        {p.text("ui.operationAccepted", {
          operationId: receipt.operationId,
        })}
      </p>
      <ReceiptFields
        subject={receipt.subject}
        fields={definition.catalog.fields}
        context={p.text("ui.acceptedContext", {
          operationId: receipt.operationId,
        })}
      />
      <Button
        onPress={() => {
          onCommitted(receipt);
        }}
      >
        {p.text("ui.returnToCase")}
      </Button>
    </section>
  );
};
