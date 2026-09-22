import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import type { Notice } from "../api/notices";
import { Button } from "react-aria-components/Button";
import { Form } from "react-aria-components/Form";
import { Input } from "react-aria-components/Input";
import { Label } from "react-aria-components/Label";
import { TextField } from "react-aria-components/TextField";
import type { DefinitionPayload, Receipt } from "../api/v2";
import { useOperationObservation } from "../hooks/useOperationObservation";
import { CaseFieldsView } from "../components/CaseFieldsView";

type OperationLookupProps = { token: string; definition: DefinitionPayload };

const ReceiptView = ({
  receipt,
  definition,
}: {
  receipt: Receipt;
  definition: DefinitionPayload;
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

export const OperationLookup = ({ token, definition }: OperationLookupProps) => {
  const p = usePresentation();
  const { operationId, setOperationId, receipt, message, notObserved, loading, observe } =
    useOperationObservation(token);
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
        <TextField value={operationId} onChange={setOperationId}>
          <Label>{p.text("ui.exactOperationId")}</Label>
          <Input autoComplete="off" dir="ltr" />
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
