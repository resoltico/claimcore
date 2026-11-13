import type { MutationLockChange } from "../hooks/useWorkspaceMutationLock";
import type { CaseSelector, HumanReadDocument_caseGet, Screen } from "@contracts/client";
import { useState, type ReactNode } from "react";
import { useSessionContext } from "../session/SessionContext";
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
  unavailableAct?: ActKind;
  reloadSignal: number;
  definition: CatalogDocument;
  onBack: () => void;
  onMutationLockChange?: MutationLockChange;
  onAct: (current: CurrentCase, act: ActKind, paymentId?: string) => void;
};

const CurrentFeedback = ({
  message,
  loading,
  onRetry,
}: {
  message: Notice | null;
  loading: boolean;
  onRetry: () => void;
}) => {
  const p = usePresentation();
  return (
    <>
      {message === null ? null : (
        <p className="error" role="alert">
          <NoticeView value={message} />
        </p>
      )}
      {message === null ||
      (message.kind === "human" &&
        !["RETRY_SAFE", "READ_CURRENT", "REVIEW_LATEST"].includes(message.advice.action)) ? null : (
        <Button onPress={onRetry}>{p.text("ui.tryAgain")}</Button>
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
  onMutationLockChange: MutationLockChange;
  onAct: CaseDetailProps["onAct"];
}) => {
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
        onChanged={onChanged}
        onAct={(act, paymentId) => {
          onAct(current, act, paymentId);
        }}
      />
    ) : null;
  return (
    <>
      <div className="case-workspace">
        <div className="case-main-column">
          {facts}
          {panels.notes}
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

const asCurrentCase = (state: HumanReadDocument_caseGet | null) =>
  state !== null && isCurrentCase(state) ? state : null;
const CaseHeader = ({
  target,
  onBack,
  state,
  onAct,
}: Pick<CaseDetailProps, "target" | "onBack" | "onAct"> & {
  state: HumanReadDocument_caseGet | null;
}) => {
  const p = usePresentation();
  const current = asCurrentCase(state);
  const identity = state?.screen.regions.find((region) => region.role === "IDENTITY");
  const attention = state?.screen.regions.find((region) => region.role === "ATTENTION");
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
            timeZone={state?.basis.kind === "SNAPSHOT" ? state.basis.observation.timeZone : "UTC"}
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
          <ScreenView
            view={state.screen}
            roles={["PROGRESS"]}
            timeZone={state.basis.kind === "SNAPSHOT" ? state.basis.observation.timeZone : "UTC"}
          />
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
  value: ActKind;
  state: HumanReadDocument_caseGet;
  onAct: CaseDetailProps["onAct"];
}) => {
  const p = usePresentation();
  const current = asCurrentCase(state);
  return (
    <aside className="case-action-refusal" role="alert">
      <p>
        <strong>{p.actLabel(value)}</strong>: {p.text("ui.actionNotOffered")}
      </p>
      {current === null ? null : (
        <CaseActionAlternatives
          current={current}
          onAct={(act) => {
            onAct(current, act);
          }}
        />
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
  state: HumanReadDocument_caseGet | null,
  access: string,
): "ERASED" | "FULL" | "SUMMARY" => {
  if (state?.disclosure === "ERASED") {
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
      <ScreenView
        view={state.screen}
        roles={["DETAILS"]}
        timeZone={state.basis.kind === "SNAPSHOT" ? state.basis.observation.timeZone : "UTC"}
      />
    );
  return (
    <section className="case-detail" aria-labelledby="case-detail-title">
      <CaseHeader target={target} onBack={onBack} state={state} onAct={onAct} />
      <CurrentFeedback
        message={current.message}
        loading={current.loading}
        onRetry={() => {
          current.revalidate();
          document.getElementById("case-detail-title")?.focus();
        }}
      />
      {state?.screen.regions
        .filter((region) => region.role === "NOTICE")
        .map((region) => (
          <RegionView
            key={region.role}
            region={region}
            timeZone={state.basis.kind === "SNAPSHOT" ? state.basis.observation.timeZone : "UTC"}
          />
        ))}
      {state === null || unavailableAct === undefined ? null : (
        <UnavailableAct value={unavailableAct} state={state} onAct={onAct} />
      )}
      {mounted === null ? null : (
        <CurrentPresentation
          current={mounted}
          timeline={timeline}
          recordDetails={recordDetails}
          disclosureAvailable={lookup !== null}
          view={current.screen}
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

export const CaseDetail = (props: CaseDetailProps) => {
  const { state } = useSessionContext();
  return state.kind === "authenticated" ? (
    <CaseDisclosure
      key={JSON.stringify([state.epoch, state.token, props.target, props.reloadSignal])}
      {...props}
    />
  ) : null;
};
