import { useEffect, useRef, useState, type RefObject } from "react";
import { Button } from "react-aria-components/Button";
import { FieldError } from "react-aria-components/FieldError";
import { Input } from "react-aria-components/Input";
import { Label } from "react-aria-components/Label";
import { TextField } from "react-aria-components/TextField";
import type { Notice } from "../../api/notices";
import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";
import type { RecoveryActions } from "./RecoveryState";

type LookupProps = { busy: boolean; actions: RecoveryActions; message: Notice | null };
const useIdentityInputs = (message: Notice | null) => {
  const [operationId, setOperationId] = useState("");
  const [requestSha256, setRequestSha256] = useState("");
  const idInput = useRef<HTMLInputElement>(null);
  const digestInput = useRef<HTMLInputElement>(null);
  const invalidId =
    message?.kind === "diagnostic" && message.diagnostic.id === "WEB_INPUT_INVALID_UUID";
  const invalidDigest =
    message?.kind === "diagnostic" && message.diagnostic.id === "WEB_INPUT_INVALID_DIGEST";
  useEffect(() => {
    if (invalidId) {
      idInput.current?.focus();
    } else if (invalidDigest) {
      digestInput.current?.focus();
    }
  }, [invalidId, invalidDigest]);
  return {
    operationId,
    setOperationId,
    requestSha256,
    setRequestSha256,
    idInput,
    digestInput,
    invalidId,
    invalidDigest,
  };
};

type TechnicalFieldProps = {
  label: string;
  value: string;
  disabled: boolean;
  error: Notice | null;
  input: RefObject<HTMLInputElement | null>;
  onChange: (value: string) => void;
};
const TechnicalField = ({
  label,
  value,
  disabled,
  error,
  input,
  onChange,
}: TechnicalFieldProps) => (
  <TextField value={value} isDisabled={disabled} isInvalid={error !== null} onChange={onChange}>
    <Label>{label}</Label>
    <Input ref={input} autoComplete="off" dir="ltr" />
    {error === null ? null : (
      <FieldError>
        <NoticeView value={error} />
      </FieldError>
    )}
  </TextField>
);
const IdentityFields = ({
  model,
  disabled,
  actions,
  message,
}: {
  model: ReturnType<typeof useIdentityInputs>;
  disabled: boolean;
  actions: RecoveryActions;
  message: Notice | null;
}) => {
  const p = usePresentation();
  return (
    <>
      <TechnicalField
        label={p.text("ui.exactOperationId")}
        value={model.operationId}
        disabled={disabled}
        error={model.invalidId ? message : null}
        input={model.idInput}
        onChange={(value) => {
          model.setOperationId(value);
          actions.clearMessage();
        }}
      />
      <TechnicalField
        label={p.text("ui.requestDigest")}
        value={model.requestSha256}
        disabled={disabled}
        error={model.invalidDigest ? message : null}
        input={model.digestInput}
        onChange={(value) => {
          model.setRequestSha256(value);
          actions.clearMessage();
        }}
      />
    </>
  );
};

export const RecoveryIdentityLookup = ({ busy, actions, message }: LookupProps) => {
  const p = usePresentation();
  const model = useIdentityInputs(message);
  const { operationId, requestSha256 } = model;
  return (
    <div className="lookup">
      <IdentityFields model={model} disabled={busy} actions={actions} message={message} />
      <Button
        isDisabled={busy || operationId === ""}
        onPress={() => {
          actions.inspectId(operationId);
        }}
      >
        {p.text("ui.inspectOperationId")}
      </Button>
      <Button
        isDisabled={busy || operationId === "" || requestSha256 === ""}
        onPress={() => {
          actions.exportIdentity(operationId, requestSha256);
        }}
      >
        {p.text("ui.exportKnownIdentity")}
      </Button>
    </div>
  );
};
