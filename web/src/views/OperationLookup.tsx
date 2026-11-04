import { IdentifierValue } from "../components/IdentifierValue";
import { actorName } from "../presentation/actorName";
import { renderCatalogKey } from "../presentation/messages";
import { operationEntryForInput } from "../protocol/operationCatalog";
import { WorkReceiptView } from "./work/WorkReceiptView";
import { IdentifierMessage } from "../presentation/IdentifierMessage";
import { navigate } from "../routing/navigation";
import { NoticeView } from "../presentation/Message";
import { FieldError } from "react-aria-components/FieldError";
import { useEffect, useRef } from "react";
import { usePresentation } from "../presentation/context";
import type { Notice } from "../protocol/notices";
import { Button } from "react-aria-components/Button";
import { Form } from "react-aria-components/Form";
import { Input } from "react-aria-components/Input";
import { Label } from "react-aria-components/Label";
import { TextField } from "react-aria-components/TextField";
import type { ProductMetadata, OperationReceipt } from "../api/service";
import { useOperationObservation } from "../hooks/useOperationObservation";
import { ReceiptFields } from "../components/ReceiptFields";
type OperationLookupProps = {
  definition: ProductMetadata;
  initialOperationId?: string;
  onLockChange?: (locked: boolean) => void;
};
const ReceiptBody = ({
  receipt,
  definition,
}: {
  receipt: OperationReceipt;
  definition: ProductMetadata;
}) => {
  const p = usePresentation();
  const actLabel =
    receipt.subject.kind === "REMINDER"
      ? renderCatalogKey(
          p,
          operationEntryForInput({ kind: receipt.subject.kind, act: { kind: receipt.subject.act } })
            .labelId,
        )
      : p.term("act", receipt.subject.act);
  if (receipt.subject.kind === "AUTHORITY") {
    const target = receipt.subject.targetActor;
    const id = target.kind === "DISCLOSED" ? target.actor.actorId : target.actorId;
    return (
      <section className="authority-receipt">
        {target.kind === "DISCLOSED" ? (
          <p>
            {p.text("ui.authorityReceipt", { act: actLabel, actor: actorName(p, target.actor) })}
          </p>
        ) : (
          <>
            <p>{actLabel}</p>
            <p>{p.text("ui.snapshotWithheld")}</p>
          </>
        )}
        <details>
          <summary>{p.text("ui.technicalDetails")}</summary>
          <IdentifierValue value={id} />
        </details>
      </section>
    );
  }
  if (receipt.subject.kind === "WORK" || receipt.subject.kind === "REMINDER") {
    return <WorkReceiptView receipt={receipt} />;
  }
  return (
    <ReceiptFields
      subject={receipt.subject}
      fields={definition.catalog.fields}
      context={
        <IdentifierMessage
          id="ui.observedContext"
          values={{ operationId: receipt.operationId }}
          argument="operationId"
        />
      }
    />
  );
};
const ReceiptView = ({
  receipt,
  definition,
}: {
  receipt: OperationReceipt;
  definition: ProductMetadata;
}) => {
  const p = usePresentation();
  return (
    <>
      <p>
        <IdentifierMessage
          id="ui.observedReceipt"
          values={{
            operationId: receipt.operationId,
            act:
              receipt.subject.kind === "REMINDER"
                ? renderCatalogKey(
                    p,
                    operationEntryForInput({
                      kind: receipt.subject.kind,
                      act: { kind: receipt.subject.act },
                    }).labelId,
                  )
                : p.term("act", receipt.subject.act),
            actor: actorName(p, receipt.recordedBy),
          }}
          argument="operationId"
        />
      </p>
      <ReceiptBody receipt={receipt} definition={definition} />
    </>
  );
};
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
