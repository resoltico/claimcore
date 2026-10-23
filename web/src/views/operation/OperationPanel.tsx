import { useEffect, useRef, useState } from "react";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import { CopyValue } from "../../components/CopyValue";
import { AccessibleModal } from "../../components/AccessibleModal";
import { OperationForm, type OperationFormProps } from "./OperationForm";
import { OperationReview } from "./OperationReview";
import type { OperationModel } from "../../hooks/operation/useOperation";

type Discard = { model: OperationModel; onDiscard: () => void };
const DiscardButton = ({ model, onDiscard }: Discard) => {
  const p = usePresentation();
  return (
    <button type="button" disabled={model.busy} onClick={onDiscard}>
      {p.text("ui.discardAndLeave")}
    </button>
  );
};
const UnknownOperation = ({ model, onDiscard }: Discard) => {
  const p = usePresentation();
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    if (!model.busy) {
      heading.current?.focus();
    }
  }, [model.busy]);
  const input = model.prepared?.input;
  const metadata: ReadonlyArray<readonly [string, string]> =
    input === undefined
      ? []
      : [
          ["operationId", input.operationId],
          ["act", input.act.kind],
          ...(input.scope === "CASE"
            ? ([
                ["caseReference", input.caseReference],
                ["expectedRevision", String(input.expectedRevision)],
              ] as const)
            : []),
        ];
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
      <button type="button" disabled={model.busy} onClick={() => void model.execute()}>
        {p.text("ui.retryExact")}
      </button>
      <DiscardButton model={model} onDiscard={onDiscard} />
    </section>
  );
};
type Props = Omit<OperationFormProps, "onCommand">;
type DialogProps = Props & { pending: string | null; setPending: (kind: string | null) => void };
const DiscardDialog = ({ model, onClose, pending, setPending }: DialogProps) => {
  const p = usePresentation();
  let description = "ui.changeCommandDescription" as
    "ui.changeCommandDescription" | "ui.discardUnknownDescription" | "ui.leaveDraftDescription";
  if (model.priorUncertain) {
    description = "ui.discardUnknownDescription";
  } else if (pending === "LEAVE") {
    description = "ui.leaveDraftDescription";
  }
  return (
    <AccessibleModal
      title={p.text("ui.changeCommandTitle")}
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
const PanelContent = (props: DialogProps) => {
  const { model, onClose, setPending } = props;
  const p = usePresentation();
  const requestDiscard = () => {
    setPending("LEAVE");
  };
  if (model.priorUncertain) {
    return <UnknownOperation model={model} onDiscard={requestDiscard} />;
  }
  if (model.disclosureRefused || model.contextChanged || !model.disclosureCurrent) {
    return (
      <section role="alert">
        <NoticeView value={model.notice ?? { kind: "local", reason: "incomplete" }} />
        {!model.disclosureRefused && !model.contextChanged ? (
          <button type="button" disabled={model.busy} onClick={() => void model.preview()}>
            {p.text("ui.previewOperation")}
          </button>
        ) : null}
        <DiscardButton model={model} onDiscard={requestDiscard} />
      </section>
    );
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
        onCommand={requestChange}
      />
      {onClose === undefined && model.dirty ? (
        <DiscardButton model={model} onDiscard={requestDiscard} />
      ) : null}
      <OperationReview model={model} />
    </>
  );
};
export const OperationPanel = (props: Props) => {
  const [pending, setPending] = useState<string | null>(null);
  return (
    <>
      <PanelContent {...props} pending={pending} setPending={setPending} />
      <DiscardDialog {...props} pending={pending} setPending={setPending} />
    </>
  );
};
