import type { CaseSelector, CaseView, CaseActRefusal, PresentedView } from "@contracts/client";
import { useRequesterContext } from "../hooks/useRequesterContext";
import { MaintenanceNotice } from "../components/MaintenanceNotice";
import { useState, type ReactNode } from "react";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import type { Notice } from "../protocol/notices";
import { ButtonControl as Button } from "../ui/ButtonControl";
import type { CurrentCase, CatalogDocument } from "../api/service";
import { isCurrentCase } from "../api/service";

import { useCaseWorkPanels } from "./work/useCaseWorkPanels";
import { useWide } from "../hooks/useWide";
import { RecordView } from "../ui/RecordView";
import { CaseActions, CasePrimaryAction, CaseActionAlternatives } from "./case/CaseActions";
import { CaseProgress } from "./case/CaseProgress";
import { CharacterWarning } from "../components/BusinessValue";
import "./case.css";
import { type ActKind } from "../operation/metadata";
import { useCaseDisclosure } from "../hooks/useCaseDisclosure";
import { CaseTimeline, type TimelineFilter } from "./case/CaseTimeline";
import { CaseHeadline } from "../components/CaseHeadline";
import { CaseAttention } from "../components/CaseAttention";

type CaseDetailProps = {
  target: CaseSelector;
  unavailableAct?: CaseActRefusal;
  reloadSignal: number;
  definition: CatalogDocument;
  onBack: () => void;
  onMutationLockChange?: (locked: boolean) => void;
  onAct: (current: CurrentCase, act: ActKind) => void;
};

const CurrentFeedback = ({ message, loading }: { message: Notice | null; loading: boolean }) => {
  const p = usePresentation();
  return (
    <>
      {message === null ? null : (
        <p className="error" role="alert">
          <NoticeView value={message} />
        </p>
      )}
      {loading ? <p role="status">{p.text("ui.loadingCurrentCase")}</p> : null}
    </>
  );
};

const CurrentPresentation = ({
  current,
  view,
  onChanged,
  onMutationLockChange,
  disclosureAvailable,
  timeline,
  recordDetails,
}: {
  timeline: ReactNode;
  recordDetails: ReactNode;
  current: CurrentCase;
  view: PresentedView | null;
  onChanged: () => void;
  disclosureAvailable: boolean;
  onMutationLockChange: (locked: boolean) => void;
  onAct: CaseDetailProps["onAct"];
}) => {
  const globalPaused = useRequesterContext()?.maintenance.active === true;
  const wide = useWide();
  const panels = useCaseWorkPanels({
    current,
    onChanged,
    onLockChange: onMutationLockChange,
    disclosureAvailable,
  });
  const facts =
    disclosureAvailable && view?.kind === "RECORD" ? (
      <RecordView view={view} timeZone={current.work.basis.timeZone} />
    ) : null;
  return (
    <>
      {disclosureAvailable ? (
        <MaintenanceNotice active={current.advice.maintenance.active && !globalPaused} />
      ) : null}
      {wide ? (
        <div className="case-workspace">
          <div className="case-main-column">
            {facts}
            {timeline}
          </div>
          <aside className="case-work-rail">
            {panels.nextActions}
            {panels.work}
            {recordDetails}
          </aside>
        </div>
      ) : (
        <div className="case-workspace case-workspace-compact">
          {panels.nextActions}
          {facts}
          {panels.work}
          {timeline}
          {recordDetails}
        </div>
      )}
      {panels.operation}
    </>
  );
};

const asCurrentCase = (state: (CaseView & { readonly view: PresentedView }) | null) =>
  state !== null && isCurrentCase(state) ? state : null;
const CaseHeader = ({
  target,
  onBack,
  state,
  onAct,
}: Pick<CaseDetailProps, "target" | "onBack" | "onAct"> & {
  state: (CaseView & { readonly view: PresentedView }) | null;
}) => {
  const p = usePresentation();
  const caseReference =
    state?.caseReference ??
    (target.kind === "REFERENCE" ? target.caseReference : p.text("ui.caseDetail"));
  const current = asCurrentCase(state);

  return (
    <>
      <div className="case-back">
        <Button onPress={onBack}>{p.text("ui.backToCases")}</Button>
      </div>
      <div className="section-heading case-heading">
        <div>
          <p className="eyebrow">
            <bdi dir="ltr" className="identifier-value">
              {caseReference}
            </bdi>
            <CharacterWarning value={caseReference} />
          </p>
          <h1 id="case-detail-title" tabIndex={-1}>
            <bdi>{p.text("ui.caseDetail")}</bdi>
          </h1>
          {state === null ? null : (
            <div className="case-header-state">
              <CaseHeadline value={state.headline} />
              <CaseAttention values={state.attention} />
              {state.mine ? <span className="assigned-marker">{p.text("ui.mine")}</span> : null}
            </div>
          )}
        </div>
        {current === null ? null : (
          <div className="case-header-actions">
            <CasePrimaryAction
              current={current}
              onAct={(act) => {
                onAct(current, act);
              }}
            />
            <CaseActions
              current={current}
              onAct={(act) => {
                onAct(current, act);
              }}
            />
          </div>
        )}
      </div>
    </>
  );
};

const UnavailableAct = ({
  value,
  state,
  onAct,
}: {
  value: CaseActRefusal;
  state: CaseView & { readonly view: PresentedView };
  onAct: CaseDetailProps["onAct"];
}) => {
  const p = usePresentation();
  const current = asCurrentCase(state);
  const alternative = state.advice.recommended;
  return (
    <aside className="case-action-refusal" role="alert">
      <p>
        <strong>{p.actLabel(value.act)}</strong>:{" "}
        <NoticeView value={{ kind: "diagnostic", diagnostic: value.reason }} />
      </p>
      {current !== null && alternative === null ? (
        <CaseActionAlternatives
          current={current}
          onAct={(act) => {
            onAct(current, act);
          }}
        />
      ) : null}
      {current === null || alternative === null ? null : (
        <Button
          onPress={() => {
            onAct(current, alternative);
          }}
        >
          {p.actLabel(alternative)}
        </Button>
      )}
    </aside>
  );
};
const ignoreMutationLock = (_locked: boolean): void => undefined;
const useRetainedCase = (lookup: CurrentCase | null) => {
  const [retained, setRetained] = useState<CurrentCase | null>(null);
  if (lookup !== null && lookup !== retained) {
    setRetained(lookup);
  }
  return lookup ?? retained;
};
const useTimelineSelection = (access: string) => {
  const [selection, setSelection] = useState<{ access: string; value: TimelineFilter }>({
    access,
    value: "ALL",
  });
  if (selection.access !== access) {
    setSelection({ access, value: "ALL" });
  }
  return {
    value: selection.access === access ? selection.value : "ALL",
    change: (value: TimelineFilter) => {
      setSelection({ access, value });
    },
  };
};
const ErasedRecord = ({ state, view }: { state: CaseView | null; view: PresentedView | null }) =>
  state?.state.kind === "ERASED" && view?.kind === "RECORD" ? (
    <div className="case-lifecycle-erased">
      <RecordView view={view} timeZone={state.work.basis.timeZone} />
    </div>
  ) : null;
const CaseDisclosure = ({
  target,
  onBack,
  onAct,
  onMutationLockChange = ignoreMutationLock,
  unavailableAct,
}: CaseDetailProps) => {
  const p = usePresentation();
  const current = useCaseDisclosure(target);
  const { historyAccess } = current;
  const timelineSelection = useTimelineSelection(historyAccess);
  const state = current.value;
  const lookup = asCurrentCase(state);
  const mounted = useRetainedCase(lookup);
  const timeline =
    current.historyAccess === "NONE" ? null : (
      <CaseTimeline
        key={historyAccess}
        target={target}
        selection={timelineSelection}
        onDisclosureRefused={current.revalidate}
      />
    );
  const recordDetails = null;
  return (
    <section className="case-detail" aria-labelledby="case-detail-title">
      <CaseHeader target={target} onBack={onBack} state={state} onAct={onAct} />
      <CurrentFeedback message={current.message} loading={current.loading} />
      {state === null || unavailableAct === undefined ? null : (
        <UnavailableAct value={unavailableAct} state={state} onAct={onAct} />
      )}
      {state === null ? null : <CaseProgress value={state.stages} />}
      {current.message === null ? null : (
        <Button
          onPress={() => {
            current.revalidate();
            document.getElementById("case-detail-title")?.focus();
          }}
        >
          {p.text("ui.tryAgain")}
        </Button>
      )}
      {mounted === null ? null : (
        <CurrentPresentation
          current={mounted}
          timeline={timeline}
          recordDetails={recordDetails}
          disclosureAvailable={lookup !== null}
          view={current.view}
          onChanged={current.revalidate}
          onMutationLockChange={onMutationLockChange}
          onAct={onAct}
        />
      )}
      <ErasedRecord state={state} view={current.view} />
      {mounted === null ? recordDetails : null}
      {mounted === null ? timeline : null}
    </section>
  );
};

export const CaseDetail = (props: CaseDetailProps) => (
  <CaseDisclosure key={JSON.stringify([props.target, props.reloadSignal])} {...props} />
);
