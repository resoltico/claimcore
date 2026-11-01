import { IdentifierValue } from "../../components/IdentifierValue";
import { useEffect, useRef } from "react";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import { CopyValue } from "../../components/CopyValue";
import { AccessibleModal } from "../../components/AccessibleModal";
import { OperationForm, type OperationFormProps } from "./OperationForm";
import { OperationReview } from "./OperationReview";
import type { OperationModel } from "../../hooks/operation/useOperation";
type Discard = {
  model: OperationModel;
  onDiscard: () => void;
  hidden: boolean;
};
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
    ...(input.kind === "CASE"
      ? ([
          [
            input.target.kind === "REFERENCE" ? "caseReference" : "caseId",
            input.target.kind === "REFERENCE" ? input.target.caseReference : input.target.caseId,
          ],
          ["expectedRevision", String(input.expectedRevision)],
        ] as const)
      : []),
  ];
};
const PendingMetadata = ({ metadata }: { metadata: ReturnType<typeof pendingMetadata> }) => {
  const p = usePresentation();
  return (
    <dl>
      {metadata.map(([name, value]) => (
        <div key={name}>
          <dt>{p.fieldLabel(name)}</dt>
          <dd>
            {["caseReference", "caseId", "operationId"].includes(name) ? (
              <IdentifierValue value={value} />
            ) : (
              <bdi>{value}</bdi>
            )}{" "}
            <CopyValue label={p.fieldLabel(name)} value={value} />
          </dd>
        </div>
      ))}
    </dl>
  );
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
        <NoticeView
          value={{
            kind: "local",
            reason: "outcomeUnknown",
          }}
        />
      </h2>
      <PendingMetadata metadata={metadata} />
      <p>{p.text("ui.retryExactDescription")}</p>
      {notice === null ? null : (
        <p>
          <NoticeView value={notice} />
        </p>
      )}
      <button type="button" disabled={model.busy} onClick={() => void model.execute()}>
        {p.text("ui.retryExact")}
      </button>
      <button className="primary" type="button" disabled={model.busy} onClick={model.checkStatus}>
        {p.text("ui.checkOutcome")}
      </button>
    </section>
  );
};
type Props = Omit<OperationFormProps, "onAct"> & { disclosureAvailable?: boolean };
type DiscardProps = {
  model: OperationModel;
  onClose?: () => void;
};
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
const UnavailableDisclosure = ({
  model,
  available = true,
}: {
  model: OperationModel;
  available?: boolean;
}) => {
  const p = usePresentation();
  let reload = null;
  if (available && !model.contextChanged) {
    if (model.canRevalidateCase) {
      reload = (
        <button type="button" disabled={model.revalidating} onClick={model.revalidateDisclosure}>
          {p.text("ui.reloadCurrentCase")}
        </button>
      );
    } else if (!model.disclosureRefused) {
      reload = (
        <button
          type="button"
          disabled={model.busy}
          onClick={() => {
            void model.preview();
          }}
        >
          {p.text("label.reviewTitle")}
        </button>
      );
    }
  }
  return (
    <section role="alert">
      <NoticeView
        value={
          available
            ? (model.notice ?? { kind: "local", reason: "incomplete" })
            : {
                kind: "diagnostic",
                diagnostic: { code: "RESOURCE_UNAVAILABLE", action: "NONE_REQUIRED" },
              }
        }
      />
      {reload}
    </section>
  );
};
const EditableOperation = (props: Props) => {
  const { model, onClose } = props;
  const requestChange = (kind: string) => {
    if (model.dirty || model.prepared !== null) {
      model.requestDiscard(kind);
    } else {
      model.discard(false, kind);
    }
  };
  const leave = () => {
    if (model.dirty || model.prepared !== null) {
      model.requestDiscard("LEAVE");
    } else {
      onClose?.();
    }
  };
  return (
    <>
      <OperationForm
        {...props}
        {...(onClose === undefined
          ? {}
          : {
              onClose: leave,
            })}
        onAct={requestChange}
      />
      <OperationReview model={model} />
    </>
  );
};

export const OperationPanel = (props: Props) => {
  const { model, disclosureAvailable = true } = props;
  const p = usePresentation();
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
  if (
    !disclosureAvailable ||
    model.disclosureRefused ||
    model.contextChanged ||
    !model.disclosureCurrent
  ) {
    return <UnavailableDisclosure model={model} available={disclosureAvailable} />;
  }
  return <EditableOperation {...props} />;
};
/** Mutation consent stays mounted when independently authorized read panes are replaced. */
export const OperationDiscard = (props: DiscardProps) => {
  const { model } = props;
  const show =
    model.priorUncertain ||
    model.disclosureRefused ||
    model.contextChanged ||
    !model.disclosureCurrent ||
    model.dirty;
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
