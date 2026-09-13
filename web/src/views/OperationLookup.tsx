import { Button } from "react-aria-components/Button";
import { Form } from "react-aria-components/Form";
import { Input } from "react-aria-components/Input";
import { Label } from "react-aria-components/Label";
import { TextField } from "react-aria-components/TextField";
import { useState } from "react";
import type { DefinitionPayload, Receipt } from "../api/v2";
import { resultMessage, v2 } from "../api/v2";
import { CaseFieldsView } from "../components/CaseFieldsView";

type OperationLookupProps = { token: string; definition: DefinitionPayload };

const ReceiptView = ({
  receipt,
  definition,
}: {
  receipt: Receipt;
  definition: DefinitionPayload;
}) => (
  <>
    <p>
      Accepted operation <bdi>{receipt.operationId}</bdi> · {receipt.command} · recorded by{" "}
      <bdi>{receipt.recordedBy}</bdi>
    </p>
    <CaseFieldsView
      caseView={receipt.snapshot}
      fields={definition.definition.fields}
      context={`observed operation ${receipt.operationId}`}
    />
  </>
);

const ObservationFeedback = ({
  message,
  notObserved,
}: {
  message: string | null;
  notObserved: boolean;
}) => (
  <>
    {message === null ? null : (
      <p className="error" role="alert">
        {message}
      </p>
    )}
    {notObserved ? <p role="status">Operation was not observed.</p> : null}
  </>
);

export const OperationLookup = ({ token, definition }: OperationLookupProps) => {
  const [operationId, setOperationId] = useState("");
  const [receipt, setReceipt] = useState<Receipt | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [notObserved, setNotObserved] = useState(false);
  const [loading, setLoading] = useState(false);
  const observe = async (): Promise<void> => {
    setLoading(true);
    setNotObserved(false);
    setReceipt(null);
    setMessage(null);
    const result = await v2.observe(operationId, token);
    const found =
      result.kind === "outcome" &&
      result.value.outcome.tag === "SUCCEEDED" &&
      result.value.outcome.data.tag === "FOUND"
        ? result.value.outcome.data.receipt
        : null;
    const missing =
      result.kind === "outcome" &&
      result.value.outcome.tag === "SUCCEEDED" &&
      result.value.outcome.data.tag === "NOT_FOUND";
    setLoading(false);
    setReceipt(found);
    setNotObserved(missing);
    setMessage(found === null && !missing ? resultMessage(result) : null);
  };
  return (
    <section aria-labelledby="operation-lookup-title">
      <h2 id="operation-lookup-title">Operation lookup</h2>
      <Form
        className="lookup"
        onSubmit={(event) => {
          event.preventDefault();
          void observe();
        }}
      >
        <TextField value={operationId} onChange={setOperationId}>
          <Label>Exact operation ID</Label>
          <Input autoComplete="off" />
        </TextField>
        <Button type="submit" isDisabled={loading || operationId === ""}>
          {loading ? "Looking up…" : "Observe operation"}
        </Button>
      </Form>
      <ObservationFeedback message={message} notObserved={notObserved} />
      {receipt === null ? null : <ReceiptView receipt={receipt} definition={definition} />}
    </section>
  );
};
