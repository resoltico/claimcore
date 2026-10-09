import { NoticeView } from "../presentation/Message";
import { FieldError } from "react-aria-components/FieldError";
import { useEffect, useRef } from "react";
import { usePresentation } from "../presentation/context";
import type { Notice } from "../api/notices";
import { Button } from "react-aria-components/Button";
import { Form } from "react-aria-components/Form";
import { Input } from "react-aria-components/Input";
import { Label } from "react-aria-components/Label";
import { TextField } from "react-aria-components/TextField";
import type { PublicDefinition, Receipt } from "../api/v3";
import { useOperationObservation } from "../hooks/useOperationObservation";
import { CaseFieldsView } from "../components/CaseFieldsView";

type OperationLookupProps = {
  token: string;
  definition: PublicDefinition;
  initialOperationId?: string;
};

const ReceiptView = ({
  receipt,
  definition,
}: {
  receipt: Receipt;
  definition: PublicDefinition;
}) => {
  const p = usePresentation();
  return (
    <>
      <p>
        {p.text("ui.observedReceipt", {
          operationId: receipt.operationId,
          command: p.commandLabel(receipt.command),
          actor: receipt.recordedBy,
        })}
      </p>
      <CaseFieldsView
        caseView={receipt.snapshot}
        fields={definition.definition.fields}
        context={p.text("ui.observedContext", { operationId: receipt.operationId })}
      />
    </>
  );
};

const ObservationFeedback = ({
  message,
  notObserved,
}: {
  message: Notice | null;
  notObserved: boolean;
}) => {
  const p = usePresentation();
  return (
    <>
      {message === null ? null : (
        <p className="error" role="alert">
          <NoticeView value={message} />
        </p>
      )}
      {notObserved ? <p role="status">{p.text("ui.operationNotObserved")}</p> : null}
    </>
  );
};

export const OperationLookup = ({
  token,
  definition,
  initialOperationId,
}: OperationLookupProps) => {
  const p = usePresentation();
  const { operationId, setOperationId, receipt, message, notObserved, loading, observe } =
    useOperationObservation(token, initialOperationId);
  const invalid =
    message?.kind === "diagnostic" && message.diagnostic.id === "WEB_INPUT_INVALID_UUID";
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
          void observe();
        }}
      >
        <TextField value={operationId} onChange={setOperationId} isInvalid={invalid}>
          <Label>{p.text("ui.exactOperationId")}</Label>
          <Input ref={input} autoComplete="off" dir="ltr" />
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
      <ObservationFeedback message={message} notObserved={notObserved} />
      {receipt === null ? null : <ReceiptView receipt={receipt} definition={definition} />}
    </section>
  );
};
