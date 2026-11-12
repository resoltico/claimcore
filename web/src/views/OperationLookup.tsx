import type { MutationLockChange } from "../hooks/useWorkspaceMutationLock";
import { ScreenView } from "../ui/ScreenView";
import { navigate } from "../routing/navigation";
import { NoticeView } from "../presentation/Message";
import { FieldError } from "react-aria-components/FieldError";
import { useEffect, useRef } from "react";
import { usePresentation } from "../presentation/context";
import type { Notice } from "../protocol/notices";
import { ButtonControl as Button } from "../ui/ButtonControl";
import { Form } from "react-aria-components/Form";
import { Input } from "react-aria-components/Input";
import { Label } from "react-aria-components/Label";
import { TextField } from "react-aria-components/TextField";
import type { ProductMetadata, OperationReceipt } from "../api/service";
import { useOperationObservation } from "../hooks/useOperationObservation";
type OperationLookupProps = {
  definition: ProductMetadata;
  initialOperationId?: string;
  onLockChange?: MutationLockChange;
};
const ReceiptView = ({ receipt }: { receipt: OperationReceipt; definition: ProductMetadata }) => (
  <div className="receipt">
    <ScreenView view={receipt.screen} timeZone={receipt.basis.observation.timeZone} />
  </div>
);
const ObservationFeedback = ({ message }: { message: Notice | null }) =>
  message === null ? null : (
    <p className="error" role="alert">
      <NoticeView value={message} />
    </p>
  );

export const OperationLookup = ({ definition, initialOperationId }: OperationLookupProps) => {
  const p = usePresentation();
  const { operationId, setOperationId, receipt, message, loading, observe } =
    useOperationObservation(initialOperationId);
  const invalid = message?.kind === "diagnostic" && message.diagnostic.code === "INPUT_INVALID";
  const input = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (invalid && !loading) {
      input.current?.focus();
    }
  }, [invalid, loading]);
  return (
    <section aria-labelledby="operation-lookup-title">
      <h2 id="operation-lookup-title">{p.text("ui.operationLookup")}</h2>
      <Form
        className="lookup"
        onSubmit={(event) => {
          event.preventDefault();
          if (initialOperationId === undefined) {
            void observe();
          } else {
            navigate(`/recovery/${encodeURIComponent(operationId)}`);
          }
        }}
      >
        <TextField value={operationId} onChange={setOperationId} isInvalid={invalid}>
          <Label>{p.text("ui.exactOperationId")}</Label>
          <Input ref={input} className="identifier-value" autoComplete="off" dir="ltr" />
          {!invalid || message === null ? null : (
            <FieldError>
              <NoticeView value={message} />
            </FieldError>
          )}
        </TextField>
        <Button type="submit" isDisabled={loading || operationId === ""}>
          {loading ? p.text("ui.lookingUp") : p.text("ui.observeOperation")}
        </Button>
      </Form>
      <ObservationFeedback message={message} />
      {receipt === null ? null : <ReceiptView receipt={receipt} definition={definition} />}
    </section>
  );
};
