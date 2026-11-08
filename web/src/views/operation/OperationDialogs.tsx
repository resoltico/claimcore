import { IdentifierMessage } from "../../presentation/IdentifierMessage";
import { usePresentation } from "../../presentation/context";
import { ButtonControl as Button } from "../../ui/ButtonControl";
import { useEffect, useRef } from "react";
import type { Notice } from "../../protocol/notices";
import { NoticeView } from "../../presentation/Message";
import type { ProductMetadata, CaseOperationReceipt } from "../../api/service";
import { ScreenView } from "../../ui/ScreenView";
type AcceptedOperationProps = {
  definition: ProductMetadata;
  receipt: CaseOperationReceipt;
  message: Notice | null;
  onCommitted: (receipt: CaseOperationReceipt) => void;
};
const AcceptedIdentity = ({ operationId }: { operationId: string }) => (
  <p>
    <IdentifierMessage id="ui.operationAccepted" values={{ operationId }} argument="operationId" />
  </p>
);
export const AcceptedOperation = ({ receipt, message, onCommitted }: AcceptedOperationProps) => {
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
      <ScreenView view={receipt.view} timeZone={receipt.basis.timeZone} />
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
