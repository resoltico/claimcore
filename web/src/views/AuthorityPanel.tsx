import { useCallback, useEffect, useRef } from "react";
import { productMetadata } from "@contracts/product-metadata";
import { service, type ServiceResponse } from "../api/service";
import { AuthorityListing } from "./AuthorityListing";
import { AccessibleModal } from "../components/AccessibleModal";
import { ProtocolSummary } from "../components/ProtocolSummary";
import { ProtocolFields } from "../components/ProtocolFields";
import { selectedSchema } from "../protocol/forms";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import { useRead } from "../hooks/useRead";
import {
  authorityDocument as document,
  useAuthorityOperation,
  type AuthorityOperation,
} from "../hooks/operation/useAuthorityOperation";
const contextData = (response: ServiceResponse<"context">) =>
  response.outcome.tag === "SUCCEEDED" ? response.outcome.data : null;
const authorityFields = document.$defs?.["root"]?.properties ?? {};
type Command = (typeof productMetadata.descriptors.authorityCommands)[number];
type FormProps = { model: AuthorityOperation; offered: ReadonlyArray<Command> };
const AuthorityPicker = ({ model, offered }: FormProps) => {
  const p = usePresentation();
  return (
    <label>
      {p.text("ui.command")}
      <select
        value={String(model.values["act.kind"] ?? offered[0]?.kind ?? "")}
        onChange={(event) => {
          model.changeCommand(event.target.value);
        }}
      >
        {offered.map((command) => (
          <option key={command.kind} value={command.kind}>
            {p.authorityLabel(command.kind)}
          </option>
        ))}
      </select>
    </label>
  );
};
const AuthorityInputs = ({ model, offered }: FormProps) => {
  const values = {
    ...model.values,
    "act.kind": model.values["act.kind"] ?? offered[0]?.kind ?? "",
  };
  const change = (path: string, value: string | boolean) => {
    model.setValues({ ...model.values, [path]: value });
    model.setFieldError(null);
  };
  return (
    <>
      <ProtocolFields
        document={document}
        node={authorityFields["target"] ?? {}}
        path="target"
        values={values}
        disabled={model.busy}
        errorPath={model.fieldError}
        onChange={change}
      />
      <ProtocolFields
        document={document}
        node={selectedSchema(document, authorityFields["act"] ?? {}, "act", values)}
        path="act"
        values={values}
        disabled={model.busy}
        errorPath={model.fieldError}
        onChange={change}
      />
    </>
  );
};
const AuthorityForm = ({ model, offered }: FormProps) => {
  const p = usePresentation();
  const form = useRef<HTMLFormElement>(null);
  useEffect(() => {
    const input = Array.from(
      form.current?.querySelectorAll<HTMLInputElement | HTMLSelectElement>("input,select") ?? [],
    ).find((item) => item.name === model.fieldError);
    input?.focus();
  }, [model.fieldError]);
  return (
    <form
      ref={form}
      onSubmit={(event) => {
        event.preventDefault();
        model.review();
      }}
    >
      <AuthorityPicker model={model} offered={offered} />
      {offered.length === 0 ? null : (
        <>
          <AuthorityInputs model={model} offered={offered} />
          <button type="submit">{p.text("ui.previewOperation")}</button>
          <button
            type="button"
            onClick={() => {
              model.setPendingDiscard("");
            }}
          >
            {p.text("ui.discardAndLeave")}
          </button>
        </>
      )}
    </form>
  );
};
const AuthorityDiscard = ({ model }: { model: AuthorityOperation }) => {
  const p = usePresentation();
  return (
    <AccessibleModal
      title={p.text("ui.changeCommandTitle")}
      closeLabel={p.text("ui.keepEditing")}
      description={p.text("ui.changeCommandDescription")}
      isOpen={model.pendingDiscard !== null}
      isDismissable
      onOpenChange={() => {
        model.setPendingDiscard(null);
      }}
    >
      <button type="button" onClick={model.discard}>
        {p.text("ui.discardAndChange")}
      </button>
    </AccessibleModal>
  );
};
const AuthorityReview = ({ model }: { model: AuthorityOperation }) => {
  const p = usePresentation();
  return (
    <section>
      <h3>{p.text("ui.reviewExactRequest")}</h3>
      <ProtocolSummary value={model.prepared?.input} />
      {model.uncertain ? (
        <p>{p.text("ui.retryExactDescription")}</p>
      ) : (
        <label>
          <input
            type="checkbox"
            checked={model.confirmed}
            onChange={(event) => {
              model.setConfirmed(event.target.checked);
            }}
          />
          {p.text("ui.confirmExactRequest")}
        </label>
      )}
      <button
        type="button"
        disabled={model.busy || (!model.confirmed && !model.uncertain)}
        onClick={() => void model.execute()}
      >
        {p.text(model.uncertain ? "ui.retryExact" : "ui.submitOperation")}
      </button>
      {model.uncertain ? null : (
        <button
          type="button"
          disabled={model.busy}
          onClick={() => {
            model.setBytes(null);
            model.setValues({ ...model.values, operationId: crypto.randomUUID() });
          }}
        >
          {p.text("ui.backToCases")}
        </button>
      )}
    </section>
  );
};
export const AuthorityPanel = ({
  token,
  onLockChange,
}: {
  token: string;
  onLockChange: (locked: boolean) => void;
}) => {
  const p = usePresentation();
  const context = useRead(
    useCallback((signal: AbortSignal) => service.context(token, signal), [token]),
    contextData,
    token,
  );
  const offered = productMetadata.descriptors.authorityCommands.filter(
    (command) => context.value?.offers.some((offer) => offer.definition === command.kind) === true,
  );
  const model = useAuthorityOperation(token, offered[0]?.kind ?? "", onLockChange);
  return (
    <section aria-labelledby="authority-title">
      <h2 id="authority-title">{p.text("ui.authority")}</h2>
      {model.receipt === null ? null : (
        <p role="status">
          {p.text("ui.accepted")} · {model.receipt}
        </p>
      )}
      {model.bytes === null ? (
        <AuthorityForm model={model} offered={offered} />
      ) : (
        <AuthorityReview model={model} />
      )}
      {model.notice === null ? null : (
        <p role="alert">
          <NoticeView value={model.notice} />
        </p>
      )}
      <AuthorityListing key={model.receipt} token={token} />
      <AuthorityDiscard model={model} />
    </section>
  );
};
