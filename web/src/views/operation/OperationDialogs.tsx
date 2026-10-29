import { IdentifierMessage } from "../../presentation/IdentifierMessage";
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
const AcceptedIdentity = ({ operationId }: { operationId: string }) => (
  <p>
    <IdentifierMessage id="ui.operationAccepted" values={{ operationId }} argument="operationId" />
  </p>
);
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
      <h1 ref={heading} tabIndex={-1} id="accepted-title">
        {p.text("ui.acceptedOperation")}
      </h1>
      <AcceptedIdentity operationId={receipt.operationId} />
      <ReceiptFields
        subject={receipt.subject}
        fields={definition.catalog.fields}
        context={
          <IdentifierMessage
            id="ui.acceptedContext"
            values={{
              operationId: receipt.operationId,
            }}
            argument="operationId"
          />
        }
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
