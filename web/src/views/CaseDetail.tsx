import type { CaseSelector, CaseView, CaseActRefusal, Screen } from "@contracts/client";
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
import { ScreenView } from "../ui/ScreenView";
import { CaseActions, CaseActionAlternatives } from "./case/CaseActions";
import "./case.css";
import { type ActKind } from "../operation/metadata";
import { useCaseDisclosure } from "../hooks/useCaseDisclosure";
import { CaseFactRegions } from "./case/CaseFactRegions";
import { CaseIdentity } from "./case/CaseIdentity";
import { RegionView } from "../ui/regions/RegionView";
import { CaseTimeline, type TimelineFilter } from "./case/CaseTimeline";

type CaseDetailProps = {
  target: CaseSelector;
  unavailableAct?: CaseActRefusal;
  reloadSignal: number;
  definition: CatalogDocument;
  onBack: () => void;
  onMutationLockChange?: (locked: boolean) => void;
  onAct: (current: CurrentCase, act: ActKind, paymentId?: string) => void;
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
  onAct,
}: {
  timeline: ReactNode;
  recordDetails: ReactNode;
  current: CurrentCase;
  view: Screen | null;
  onChanged: () => void;
  disclosureAvailable: boolean;
  onMutationLockChange: (locked: boolean) => void;
  onAct: CaseDetailProps["onAct"];
}) => {
  const globalPaused = useRequesterContext()?.maintenance.active === true;
  const panels = useCaseWorkPanels({
    current,
    onChanged,
    onLockChange: onMutationLockChange,
    disclosureAvailable,
  });
  const facts =
    disclosureAvailable && view !== null ? (
      <CaseFactRegions
        current={current}
        view={view}
        onAct={(act, paymentId) => {
          onAct(current, act, paymentId);
        }}
      />
    ) : null;
  return (
    <>
      {disclosureAvailable ? (
        <MaintenanceNotice active={current.advice.maintenance.active && !globalPaused} />
      ) : null}
      <div className="case-workspace">
        <div className="case-main-column">
          {facts}
          {timeline}
        </div>
        <aside className="case-work-rail">
          {panels.work}
          {panels.nextActions}
        </aside>
      </div>
      {recordDetails}
      {panels.operation}
    </>
  );
};

const asCurrentCase = (state: (CaseView & { readonly view: Screen }) | null) =>
  state !== null && isCurrentCase(state) ? state : null;
const CaseHeader = ({
  target,
  onBack,
  state,
  onAct,
}: Pick<CaseDetailProps, "target" | "onBack" | "onAct"> & {
  state: (CaseView & { readonly view: Screen }) | null;
}) => {
  const p = usePresentation();
  const current = asCurrentCase(state);
  const identity = state?.view.regions.find((region) => region.role === "IDENTITY");
  const attention = state?.view.regions.find((region) => region.role === "ATTENTION");
  return (
    <>
      <div className="case-back">
        <Button onPress={onBack}>{p.text("ui.backToCases")}</Button>
      </div>
      <section className="case-header-card">
        <div className="case-heading">
          <CaseIdentity
            region={identity}
            attention={attention}
            requestedReference={target.kind === "REFERENCE" ? target.caseReference : undefined}
            timeZone={state?.work.basis.timeZone ?? "UTC"}
          />
          {current === null ? null : (
            <div className="case-header-actions">
              <CaseActions
                current={current}
                onAct={(act) => {
                  onAct(current, act);
                }}
              />
            </div>
          )}
        </div>
        {state === null ? null : (
          <ScreenView view={state.view} roles={["PROGRESS"]} timeZone={state.work.basis.timeZone} />
        )}
      </section>
    </>
  );
};

const UnavailableAct = ({
  value,
  state,
  onAct,
}: {
  value: CaseActRefusal;
  state: CaseView & { readonly view: Screen };
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
const timelineDisclosure = (
  state: CaseView | null,
  access: string,
): "ERASED" | "FULL" | "SUMMARY" => {
  if (state?.state.kind === "ERASED") {
    return "ERASED";
  }
  return access === "FULL" ? "FULL" : "SUMMARY";
};
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
        disclosure={timelineDisclosure(state, historyAccess)}
        target={target}
        selection={timelineSelection}
        onDisclosureChanged={current.revalidate}
      />
    );
  const recordDetails =
    state === null ? null : (
      <ScreenView view={state.view} roles={["DETAILS"]} timeZone={state.work.basis.timeZone} />
    );
  return (
    <section className="case-detail" aria-labelledby="case-detail-title">
      <CaseHeader target={target} onBack={onBack} state={state} onAct={onAct} />
      <CurrentFeedback message={current.message} loading={current.loading} />
      {state?.view.regions
        .filter((region) => region.role === "NOTICE")
        .map((region) => (
          <RegionView key={region.role} region={region} timeZone={state.work.basis.timeZone} />
        ))}
      {state === null || unavailableAct === undefined ? null : (
        <UnavailableAct value={unavailableAct} state={state} onAct={onAct} />
      )}
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
      {mounted === null ? recordDetails : null}
      {mounted === null ? timeline : null}
    </section>
  );
};

export const CaseDetail = (props: CaseDetailProps) => (
  <CaseDisclosure key={JSON.stringify([props.target, props.reloadSignal])} {...props} />
);
