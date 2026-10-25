import { useEffect, useRef } from "react";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import { CopyValue } from "../../components/CopyValue";
import { AccessibleModal } from "../../components/AccessibleModal";
import { OperationForm, type OperationFormProps } from "./OperationForm";
import { OperationReview } from "./OperationReview";
import type { OperationModel } from "../../hooks/operation/useOperation";

type Discard = { model: OperationModel; onDiscard: () => void; hidden: boolean };
const DiscardButton = ({ model, onDiscard, hidden }: Discard) => {
  const p = usePresentation();
  return (
    <button type="button" hidden={hidden} disabled={model.busy} onClick={onDiscard}>
      {p.text("ui.discardAndLeave")}
    </button>
  );
};
const pendingMetadata = (model: OperationModel): ReadonlyArray<readonly [string, string]> => {
  const input = model.prepared?.input;
  if (input === undefined) {
    return [];
  }
  return [
    ["operationId", input.operationId],
    ["act", input.act.kind],
    ...(input.scope === "CASE"
      ? ([
          ["caseReference", input.caseReference],
          ["expectedRevision", String(input.expectedRevision)],
        ] as const)
      : []),
  ];
};
const UnknownOperation = ({ model }: { model: OperationModel }) => {
  const p = usePresentation();
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    if (!model.busy) {
      heading.current?.focus();
    }
  }, [model.busy]);
  const metadata = pendingMetadata(model);
  const notice =
    model.notice?.kind === "local" && model.notice.reason === "outcomeUnknown"
      ? null
      : model.notice;
  return (
    <section role="alert" aria-live="off" aria-busy={model.busy}>
      <h2 ref={heading} tabIndex={-1}>
        <NoticeView value={{ kind: "local", reason: "outcomeUnknown" }} />
      </h2>
      <dl>
        {metadata.map(([name, value]) => (
          <div key={name}>
            <dt>{p.fieldLabel(name)}</dt>
            <dd>
              <bdi>{value}</bdi> <CopyValue label={p.fieldLabel(name)} value={value} />
            </dd>
          </div>
        ))}
      </dl>
      <p>{p.text("ui.retryExactDescription")}</p>
      {notice === null ? null : (
        <p>
          <NoticeView value={notice} />
        </p>
      )}
      <button type="button" disabled={model.busy} onClick={() => void model.execute()}>
        {p.text("ui.retryExact")}
      </button>
      <button type="button" disabled={model.busy} onClick={model.resolve}>
        {p.text("ui.checkOutcome")}
      </button>
    </section>
  );
};
type Props = Omit<OperationFormProps, "onAct">;
type DiscardProps = { model: OperationModel; onClose?: () => void };
type DialogProps = DiscardProps & {
  pending: string | null;
  setPending: (kind: string | null) => void;
};
const DiscardDialog = ({ model, onClose, pending, setPending }: DialogProps) => {
  const p = usePresentation();
  let description = "ui.changeActDescription" as
    "ui.changeActDescription" | "ui.discardUnknownDescription" | "ui.leaveDraftDescription";
  if (model.priorUncertain) {
    description = "ui.discardUnknownDescription";
  } else if (pending === "LEAVE") {
    description = "ui.leaveDraftDescription";
  }
  return (
    <AccessibleModal
      title={p.text("ui.changeActTitle")}
      closeLabel={p.text("ui.keepEditing")}
      description={p.text(description)}
      isOpen={pending !== null}
      isDismissable
      onOpenChange={() => {
        setPending(null);
      }}
    >
      <button
        type="button"
        disabled={model.busy}
        onClick={() => {
          model.discard(
            model.priorUncertain,
            pending === "LEAVE" || pending === null ? undefined : pending,
          );
          if (pending === "LEAVE") {
            onClose?.();
          }
          setPending(null);
        }}
      >
        {p.text(pending === "LEAVE" ? "ui.discardAndLeave" : "ui.discardAndChange")}
      </button>
    </AccessibleModal>
  );
};
const UnavailableDisclosure = ({ model }: { model: OperationModel }) => {
  const p = usePresentation();
  return (
    <section role="alert">
      <NoticeView value={model.notice ?? { kind: "local", reason: "incomplete" }} />
      {!model.disclosureRefused && !model.contextChanged ? (
        <button type="button" disabled={model.busy} onClick={() => void model.preview()}>
          {p.text("ui.previewOperation")}
        </button>
      ) : null}
    </section>
  );
};
export const OperationPanel = (props: Props) => {
  const { model, onClose } = props;
  const setPending = model.requestDiscard;
  const p = usePresentation();
  const requestDiscard = () => {
    setPending("LEAVE");
  };
  if (model.phase === "ACCEPTED") {
    return (
      <button
        type="button"
        onClick={() => {
          model.discard();
        }}
      >
        {p.text("ui.startAnotherOperation")}
      </button>
    );
  }
  if (model.priorUncertain) {
    return <UnknownOperation model={model} />;
  }
  if (model.disclosureRefused || model.contextChanged || !model.disclosureCurrent) {
    return <UnavailableDisclosure model={model} />;
  }
  const requestChange = (kind: string) => {
    if (model.dirty || model.prepared !== null) {
      setPending(kind);
    } else {
      model.discard(false, kind);
    }
  };
  const leave = () => {
    if (model.dirty || model.prepared !== null) {
      requestDiscard();
    } else {
      onClose?.();
    }
  };
  return (
    <>
      <OperationForm
        {...props}
        {...(onClose === undefined ? {} : { onClose: leave })}
        onAct={requestChange}
      />
      <OperationReview model={model} />
    </>
  );
};
/** Mutation consent stays mounted when independently authorized read panes are replaced. */
export const OperationDiscard = (props: DiscardProps) => {
  const { model, onClose } = props;
  const show =
    model.priorUncertain ||
    model.disclosureRefused ||
    model.contextChanged ||
    !model.disclosureCurrent ||
    (onClose === undefined && model.dirty);
  return (
    <>
      <DiscardButton
        model={model}
        hidden={!show}
        onDiscard={() => {
          model.requestDiscard("LEAVE");
        }}
      />
      <DiscardDialog {...props} pending={model.pendingDiscard} setPending={model.requestDiscard} />
    </>
  );
};
