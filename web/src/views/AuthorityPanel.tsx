import { useCallback, useEffect, useRef, useState } from "react";
import { productMetadata } from "../protocol/productMetadata";
import { isDisclosureRefused } from "../api/outcomes";
import { resultNotice, service, type ServiceResponse } from "../api/service";
import { AuthorityGrants } from "./AuthorityGrants";
import { AuthorityListing } from "./AuthorityListing";
import { AccessibleModal } from "../components/AccessibleModal";
import { ProtocolSummary } from "../components/ProtocolSummary";
import { ProtocolFields } from "../components/ProtocolFields";
import { selectedSchema, resolveSchema } from "../protocol/forms";
import type { Notice } from "../api/notices";
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
const authorityFields = resolveSchema(document, document).properties ?? {};
type Command = (typeof productMetadata.descriptors.authorityCommands)[number];
type FormProps = { model: AuthorityOperation; offered: ReadonlyArray<Command> };
const AuthorityPicker = ({ model, offered }: FormProps) => {
  const p = usePresentation();
  return (
    <label>
      {p.text("ui.authorityAction")}
      <select
        disabled={model.busy || offered.length === 0}
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
    model.setNotice(null);
    model.setConfirmed(false);
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
        errorNotice={model.notice}
        onChange={change}
      />
      <ProtocolFields
        document={document}
        node={selectedSchema(document, authorityFields["act"] ?? {}, "act", values)}
        path="act"
        values={values}
        disabled={model.busy}
        errorPath={model.fieldError}
        errorNotice={model.notice}
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
        model.review(offered[0]?.kind ?? "");
      }}
    >
      <AuthorityPicker model={model} offered={offered} />
      {offered.length === 0 ? null : (
        <>
          <AuthorityInputs model={model} offered={offered} />
          <button type="submit">{p.text("ui.previewOperation")}</button>
        </>
      )}
    </form>
  );
};
const AuthorityDiscardAction = ({ model }: { model: AuthorityOperation }) => {
  const p = usePresentation();
  if (
    model.busy ||
    model.uncertain ||
    (model.bytes === null && Object.keys(model.values).length === 1)
  ) {
    return null;
  }
  return (
    <button
      type="button"
      onClick={() => {
        model.setPendingDiscard("");
      }}
    >
      {p.text("ui.discardAndLeave")}
    </button>
  );
};
const AuthorityDiscard = ({ model }: { model: AuthorityOperation }) => {
  const p = usePresentation();
  return (
    <AccessibleModal
      title={p.text("ui.changeCommandTitle")}
      closeLabel={p.text("ui.keepEditing")}
      description={p.text("ui.changeCommandDescription")}
      isOpen={model.pendingDiscard !== null && !model.busy && !model.uncertain}
      isDismissable
      onOpenChange={() => {
        model.setPendingDiscard(null);
      }}
    >
      <button type="button" disabled={model.busy || model.uncertain} onClick={model.discard}>
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
      <ProtocolSummary value={model.prepared?.input} document={document} node={document} />
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
          {p.text(
            productMetadata.descriptors.authorityCommands.find(
              (command) => command.kind === model.prepared?.input.act.kind,
            )!.confirmationId,
          )}
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
            model.edit();
          }}
        >
          {p.text("ui.editInput")}
        </button>
      )}
    </section>
  );
};
const AuthorityContextStatus = ({
  loading,
  message,
  reload,
}: {
  loading: boolean;
  message: Notice | null;
  reload: () => void;
}) => {
  const p = usePresentation();
  return (
    <>
      {loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {message === null ? null : (
        <>
          <p role="alert">
            <NoticeView value={message} />
          </p>
          <button type="button" onClick={reload}>
            {p.text("ui.tryAgain")}
          </button>
        </>
      )}
    </>
  );
};
const useAuthorityContext = (token: string, model: AuthorityOperation) => {
  const { refused, restored } = model.disclosure;
  return useRead(
    useCallback(
      async (signal: AbortSignal) => {
        const result = await service.context(token, signal);
        if (!signal.aborted) {
          if (isDisclosureRefused(result)) {
            refused(resultNotice(result));
          } else if (result.kind === "outcome" && result.value.outcome.tag === "SUCCEEDED") {
            restored("context");
          }
        }
        return result;
      },
      [token, refused, restored],
    ),
    contextData,
    token,
  );
};
const AuthorityWorkspace = ({ token, model }: { token: string; model: AuthorityOperation }) => {
  const { refused, restored } = model.disclosure;
  const [selectedActorId, setSelectedActorId] = useState<string | null>(null);
  const context = useAuthorityContext(token, model);
  const offered = productMetadata.descriptors.authorityCommands.filter(
    (command) =>
      context.value?.offers.some(
        (offer) => offer.kind === "AUTHORITY_ACT" && offer.act === command.kind,
      ) === true,
  );
  return (
    <>
      {model.bytes === null ? (
        <AuthorityForm model={model} offered={offered} />
      ) : (
        <AuthorityReview model={model} />
      )}
      <AuthorityContextStatus
        loading={context.loading}
        message={context.message}
        reload={() => {
          context.reload();
        }}
      />
      <AuthorityListing
        token={token}
        mayReadGrants={context.value?.views.includes("AUTHORITY") === true}
        onSelect={setSelectedActorId}
        onDisclosureRefused={refused}
        onDisclosureRestored={restored}
      />
      {selectedActorId === null ? null : (
        <AuthorityGrants
          key={selectedActorId}
          actorId={selectedActorId}
          token={token}
          onBack={() => {
            (
              globalThis.document.getElementById(`actor-grants-${selectedActorId}`) ??
              globalThis.document.getElementById("authority-actors-title")
            )?.focus();
            setSelectedActorId(null);
          }}
          onDisclosureRefused={refused}
        />
      )}
    </>
  );
};
const AuthorityDisclosure = ({ token, model }: { token: string; model: AuthorityOperation }) => {
  const p = usePresentation();
  if (model.disclosure.active) {
    return <AuthorityWorkspace token={token} model={model} />;
  }
  return (
    <>
      {model.bytes === null ? (
        <AuthorityForm model={model} offered={[]} />
      ) : (
        <AuthorityReview model={model} />
      )}
      <button
        type="button"
        onClick={() => {
          model.disclosure.invalidate(true);
        }}
      >
        {p.text("ui.reloadActors")}
      </button>
    </>
  );
};
const AuthorityFeedback = ({ model }: { model: AuthorityOperation }) => {
  const summary = useRef<HTMLParagraphElement>(null);
  const notice = model.disclosure.notice ?? model.notice;
  const attemptNotice = model.notice;
  useEffect(() => {
    if (attemptNotice !== null) {
      summary.current?.focus();
    }
  }, [attemptNotice]);
  return notice === null ? null : (
    <p role="alert" tabIndex={-1} ref={summary}>
      <NoticeView value={model.uncertain ? { kind: "local", reason: "outcomeUnknown" } : notice} />
    </p>
  );
};
const AuthorityReceipt = ({ identity }: { identity: string }) => {
  const p = usePresentation();
  const heading = useRef<HTMLHeadingElement>(null);
  const previous = useRef<string | null>(null);
  useEffect(() => {
    if (previous.current !== identity) {
      heading.current?.focus();
      previous.current = identity;
    }
  }, [identity]);
  return (
    <>
      <h3 ref={heading} tabIndex={-1}>
        {p.text("ui.accepted")}
      </h3>
      <p role="status">
        <bdi>{identity}</bdi>
      </p>
    </>
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
  const model = useAuthorityOperation(token, "", onLockChange);
  return (
    <section aria-labelledby="authority-title" className="authority-panel">
      <h2 id="authority-title">{p.text("ui.authority")}</h2>
      {model.receipt === null ? null : <AuthorityReceipt identity={model.receipt} />}
      <AuthorityFeedback model={model} />
      <AuthorityDisclosure
        key={`${token}:${model.disclosure.revision}`}
        token={token}
        model={model}
      />
      <AuthorityDiscardAction model={model} />
      <AuthorityDiscard model={model} />
    </section>
  );
};
