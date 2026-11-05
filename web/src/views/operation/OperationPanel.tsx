import type { ActorRef } from "@contracts/client";
import { ActorIdentity } from "../../components/ActorIdentity";
import { useSettingsDetour } from "../../hooks/SettingsDetourContext";
import { IconButton } from "../../ui/IconButton";
import { Button } from "../../ui/Button";
import { operationEntry } from "../../protocol/operationCatalog";
import { renderCatalogKey } from "../../presentation/messages";
import { ConflictChanges } from "./ConflictChanges";
import { resourceUnavailableDiagnostic } from "@contracts/client";
import { IdentifierValue } from "../../components/IdentifierValue";
import { useCallback, useEffect, useRef, useId, type ReactNode } from "react";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import { CopyValue } from "../../components/CopyValue";
import { Dialog } from "../../ui/Dialog";
import { OperationForm, OperationFormActions, type OperationFormProps } from "./OperationForm";
import { OperationReview } from "./OperationReview";
import type { OperationModel } from "../../hooks/operation/useOperation";
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
type Props = Omit<OperationFormProps, "onAct"> & {
  disclosureAvailable?: boolean;
  open?: boolean;
  actor?: ActorRef;
  completionAnchor?: React.RefObject<HTMLElement | null>;
};
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
    <Dialog
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
    </Dialog>
  );
};
const UnavailableDisclosure = ({
  model,
  available = true,
  mayPreview = false,
}: {
  model: OperationModel;
  available?: boolean;
  mayPreview?: boolean;
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
    } else if (mayPreview) {
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
                diagnostic: resourceUnavailableDiagnostic,
              }
        }
      />
      {reload}
    </section>
  );
};
const requestClose = (model: OperationModel, onClose: Props["onClose"]) => {
  if (model.busy) {
    return;
  }
  if (model.dirty || model.prepared !== null || model.priorUncertain) {
    model.requestDiscard("LEAVE");
  } else {
    onClose?.();
  }
};
const usePreviewReturnFocus = (model: OperationModel) => {
  const preview = useRef<HTMLButtonElement>(null);
  const previous = useRef(model.phase);
  useEffect(() => {
    if (
      previous.current === "REVIEWING" &&
      model.phase === "EDITING" &&
      model.notice === null &&
      preview.current?.isConnected === true &&
      !preview.current.disabled
    ) {
      preview.current.focus();
    }
    previous.current = model.phase;
  }, [model.phase, model.notice]);
  return preview;
};
const EnterOperation = (props: Props) => {
  const { model } = props;
  const requestChange = (kind: string) => {
    if (model.dirty || model.prepared !== null) {
      model.requestDiscard(kind);
    } else {
      model.discard(false, kind);
    }
  };
  return (
    <>
      {model.conflict === null ? null : (
        <ConflictChanges key={model.conflict.identity.id} model={model} snapshot={model.conflict} />
      )}
      <OperationForm {...props} onAct={requestChange} />
    </>
  );
};
const hasCurrentDisclosure = (model: OperationModel, available = true) =>
  model.ownerVerified &&
  available &&
  !model.disclosureRefused &&
  !model.contextChanged &&
  model.disclosureCurrent;
const OperationSurface = ({
  props,
  review,
}: {
  props: Props;
  review: { body: ReactNode; actions: ReactNode } | null;
}) => {
  const { model, disclosureAvailable = true } = props;
  if (model.priorUncertain) {
    return <UnknownOperation model={model} />;
  }
  if (!hasCurrentDisclosure(model, disclosureAvailable)) {
    return (
      <UnavailableDisclosure
        model={model}
        available={disclosureAvailable}
        mayPreview={model.ownerVerified && props.offered.includes(model.entry.kind)}
      />
    );
  }
  return review === null ? <EnterOperation {...props} /> : review.body;
};
const useFieldSelection = (operationId: unknown) => {
  const selection = useRef<{
    operationId: unknown;
    name: string;
    start: number;
    end: number;
    direction: "forward" | "backward" | "none";
  } | null>(null);
  const onFieldBlur = useCallback(
    (target: EventTarget | null) => {
      if (
        (target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement) &&
        target.selectionStart !== null &&
        target.selectionEnd !== null
      ) {
        selection.current = {
          operationId,
          name: target.name,
          start: target.selectionStart,
          end: target.selectionEnd,
          direction: target.selectionDirection ?? "none",
        };
      }
    },
    [operationId],
  );
  const onMount = useCallback(
    (form: HTMLFormElement) => {
      const remembered = selection.current;
      if (remembered === null || remembered.operationId !== operationId) {
        return;
      }
      const target = form.elements.namedItem(remembered.name);
      if (
        (target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement) &&
        target.selectionStart !== null
      ) {
        target.setSelectionRange(remembered.start, remembered.end, remembered.direction);
      }
    },
    [operationId],
  );
  return { onFieldBlur, onMount };
};
const targetReference = (review: OperationModel["review"], current: Props["current"]) => {
  switch (review?.subject.kind) {
    case "CASE":
      return review.subject.after.caseReference;
    case "WORK":
    case "REMINDER":
      return review.subject.caseReference;
    default:
      return current?.caseReference;
  }
};
const ReminderTarget = ({ review }: { review: OperationModel["review"] }) =>
  review?.subject.kind === "REMINDER" && review.subject.before !== null ? (
    <p>
      <bdi>{review.subject.before.action}</bdi>
    </p>
  ) : null;
const OperationTarget = ({
  model,
  actor,
  current,
}: {
  current: Props["current"];
  model: OperationModel;
  actor: ActorRef | undefined;
}) => {
  const p = usePresentation();
  const review = model.review?.subject;
  if (model.entry.subject !== "AUTHORITY") {
    const reference = targetReference(model.review, current);
    return reference === undefined ? null : (
      <div className="operation-target">
        <p>
          <IdentifierValue value={reference} />
        </p>
        <ReminderTarget review={model.review} />
      </div>
    );
  }
  const actual = review?.kind === "AUTHORITY" ? review.target?.actor : actor;
  if (actual !== null && actual !== undefined) {
    return (
      <p className="operation-target">
        <ActorIdentity actor={actual} />
      </p>
    );
  }
  return ["ADMIT", "REGISTER"].includes(model.entry.kind) ? null : (
    <p>{p.text("ui.selectedAccount")}</p>
  );
};
const OperationSteps = ({ model }: { model: OperationModel }) => {
  const p = usePresentation();
  if (
    model.priorUncertain ||
    !["EDITING", "PREVIEWING", "REVIEWING", "PREPARING", "SUBMITTING"].includes(model.phase)
  ) {
    return null;
  }
  const reviewing = model.phase !== "EDITING" && model.phase !== "PREVIEWING";
  return (
    <ol className="operation-steps">
      <li aria-current={reviewing ? undefined : "step"}>
        <span aria-hidden="true">1</span>
        {p.text("ui.enterDetails")}
      </li>
      <li aria-current={reviewing ? "step" : undefined}>
        <span aria-hidden="true">2</span>
        {p.text("label.reviewTitle")}
      </li>
    </ol>
  );
};
const OperationFooter = ({
  props,
  review,
  formId,
  preview,
  close,
}: {
  props: Props;
  review: { body: ReactNode; actions: ReactNode } | null;
  formId: string;
  preview: React.RefObject<HTMLButtonElement | null>;
  close: () => void;
}) => {
  const p = usePresentation();
  const { model, disclosureAvailable = true } = props;
  if (review !== null) {
    return review.actions;
  }
  if (model.priorUncertain || !hasCurrentDisclosure(model, disclosureAvailable)) {
    return (
      <Button variant="secondary" isDisabled={model.busy} onPress={close}>
        {p.text("ui.close")}
      </Button>
    );
  }
  return <OperationFormActions {...props} formId={formId} preview={preview} onClose={close} />;
};
export const OperationPanel = (props: Props) => {
  const { model, disclosureAvailable = true, open = true } = props;
  const p = usePresentation();
  const formId = useId();
  const preview = usePreviewReturnFocus(model);
  const settings = useSettingsDetour();
  const selection = useFieldSelection(model.values["operationId"]);
  const entry = operationEntry(model.entry);
  const close = () => {
    requestClose(model, props.onClose);
  };
  return (
    <OperationReview
      model={model}
      paused={
        props.current?.advice.maintenance.active === true ||
        !props.offered.includes(model.entry.kind)
      }
    >
      {(review) => (
        <Dialog
          title={renderCatalogKey(p, entry.labelId)}
          description={renderCatalogKey(p, entry.meaningId)}
          identity={
            <>
              <OperationTarget model={model} actor={props.actor} current={props.current} />
              <OperationSteps model={model} />
            </>
          }
          closeLabel={p.text("ui.close")}
          placement="end"
          {...(props.completionAnchor === undefined ||
          model.priorUncertain ||
          !hasCurrentDisclosure(model, disclosureAvailable)
            ? {}
            : { anchorRef: props.completionAnchor })}
          headerEnd={
            <div className="actions">
              {settings === null ? null : (
                <IconButton
                  data-open-settings
                  icon="settings"
                  label={p.text("ui.settings")}
                  isDisabled={model.busy}
                  onPress={settings.visit}
                />
              )}
              <IconButton
                icon="close"
                label={p.text("ui.close")}
                isDisabled={model.busy}
                onPress={close}
              />
            </div>
          }
          isOpen={
            settings?.paused !== true &&
            open &&
            model.pendingDiscard === null &&
            (disclosureAvailable || model.priorUncertain) &&
            model.ownerVerified &&
            model.phase !== "ACCEPTED"
          }
          isDismissable={!model.busy}
          onOpenChange={(next) => {
            if (!next) {
              close();
            }
          }}
          footer={
            <OperationFooter
              props={props}
              review={review}
              formId={formId}
              preview={preview}
              close={close}
            />
          }
        >
          <OperationSurface props={{ ...props, ...selection, formId }} review={review} />
        </Dialog>
      )}
    </OperationReview>
  );
};
/** Mutation consent stays mounted when independently authorized read panes are replaced. */
export const OperationDiscard = (props: DiscardProps) => {
  const { model } = props;
  return (
    <DiscardDialog {...props} pending={model.pendingDiscard} setPending={model.requestDiscard} />
  );
};
