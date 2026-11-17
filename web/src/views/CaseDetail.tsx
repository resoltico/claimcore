import { coreLinkPath } from "../routing/coreLinks";
import { followLink } from "../routing/followLink";
import { navigate } from "../routing/navigation";
import { renderCatalogKey } from "../presentation/messages";
import { isNarrativeCreationId, type ActKind } from "../operation/metadata";
import type { Affordance, CaseSelector, HumanDocument, Screen } from "@contracts/client";
import { useContentRoute } from "../hooks/SettingsDetourContext";
import type { MutationLockChange } from "../hooks/useWorkspaceMutationLock";

import { useState, useLayoutEffect, useRef, type ReactNode } from "react";
import { useSessionContext } from "../session/SessionContext";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import type { Notice } from "../protocol/notices";
import { ButtonControl as Button } from "../ui/ButtonControl";
import { type CurrentCase, type CatalogDocument, isCurrentCase } from "../api/service";

import { useCaseWorkPanels } from "./work/useCaseWorkPanels";
import { ScreenView } from "../ui/ScreenView";
import { CaseActions, CaseActionAlternatives } from "./case/CaseActions";
import "./case.css";

import { useCaseDisclosure } from "../hooks/useCaseDisclosure";
import { CaseFactRegions } from "./case/CaseFactRegions";
import { CaseIdentity } from "./case/CaseIdentity";
import { RegionView } from "../ui/regions/RegionView";
import { CaseTimeline, type TimelineFilter } from "./case/CaseTimeline";

type CaseDetailProps = {
  target: CaseSelector;
  unavailableAct?: ActKind;
  reloadSignal: number;
  disclosureAvailable?: boolean;
  authoringAvailable?: boolean;
  definition: CatalogDocument;
  onBack: () => void;
  onMutationLockChange?: MutationLockChange;
  onAct: (current: CurrentCase, action: Affordance) => void;
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
  authoringAvailable,
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
  authoringAvailable: boolean;
  onMutationLockChange: MutationLockChange;
  onAct: CaseDetailProps["onAct"];
}) => {
  const panels = useCaseWorkPanels({
    current,
    onChanged,
    onLockChange: onMutationLockChange,
    disclosureAvailable,
    authoringAvailable,
  });
  const facts =
    disclosureAvailable && view !== null ? (
      <CaseFactRegions
        current={current}
        view={view}
        onChanged={onChanged}
        onAct={(action) => {
          onAct(current, action);
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

const asCurrentCase = (state: HumanDocument | null) =>
  state !== null && isCurrentCase(state) ? state : null;
const CaseHeader = ({
  target,
  onBack,
  state,
  onAct,
}: Pick<CaseDetailProps, "target" | "onBack" | "onAct"> & {
  state: HumanDocument | null;
}) => {
  const p = usePresentation();
  const route = useContentRoute();
  const selectedReminder = route.screen === "case" && route.reminderId !== undefined;
  const current = asCurrentCase(state);
  const identity = state?.screen.regions.find((region) => region.role === "IDENTITY");
  const attention = state?.screen.regions.find((region) => region.role === "ATTENTION");
  return (
    <>
      <div className="case-back">
        <Button onPress={onBack}>
          {p.text(selectedReminder ? "ui.returnToWorkspace" : "ui.backToCases")}
        </Button>
      </div>
      <section className="case-header-card">
        <div className="case-heading">
          <CaseIdentity
            region={identity}
            attention={attention}
            requestedReference={target.kind === "REFERENCE" ? target.caseReference : undefined}
            timeZone={state?.basis.kind === "SNAPSHOT" ? state.basis.observation.timeZone : "UTC"}
          />
          {current?.links.map((named) => {
            const href = coreLinkPath(named.link);
            return href === undefined ? null : (
              <a
                key={JSON.stringify(named.link)}
                href={href}
                onClick={(event) => {
                  followLink(event, () => {
                    navigate(href);
                  });
                }}
              >
                {renderCatalogKey(p, named.labelId)}
              </a>
            );
          })}
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
  state: HumanDocument;
  onAct: CaseDetailProps["onAct"];
}) => {
  const p = usePresentation();
  const current = asCurrentCase(state);
  return (
    <aside className="case-action-refusal" role="alert">
      <p>
        <strong>{p.actLabel(value)}</strong>: {p.text("label.actionNotOffered")}
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
  state: HumanDocument | null,
  access: string,
): "ERASED" | "FULL" | "SUMMARY" => {
  if (state?.disclosure === "ERASED") {
    return "ERASED";
  }
  return access === "FULL" ? "FULL" : "SUMMARY";
};
// Local authoring survives loading; a known denial stays masked until a fresh offer.
const useRetainedNoteOffer = (value: HumanDocument | null, denied: boolean) => {
  const offered =
    value === null
      ? null
      : asCurrentCase(value)?.actions.some(
          (action) => isNarrativeCreationId(action.task) && action.blocked === null,
        ) === true;
  const [retained, setRetained] = useState(false);
  const eligible = denied ? false : (offered ?? retained);
  if (eligible !== retained) {
    setRetained(eligible);
  }
  return eligible;
};
const useCaseRefresh = (
  reload: number,
  available: boolean,
  loading: boolean,
  revalidate: () => void,
) => {
  const previous = useRef({ reload, available });
  useLayoutEffect(() => {
    const prior = previous.current;
    previous.current = { reload, available };
    if (prior.reload !== reload || (!prior.available && available && !loading)) {
      revalidate();
    }
  }, [reload, available, loading, revalidate]);
};
const caseReadPresentation = (
  state: HumanDocument | null,
  available: boolean,
  historyAccess: string,
  target: CaseSelector,
  timelineSelection: ReturnType<typeof useTimelineSelection>,
  revalidate: () => void,
) => {
  const timeline =
    historyAccess === "NONE" || !available ? null : (
      <CaseTimeline
        disclosure={timelineDisclosure(state, historyAccess)}
        target={target}
        selection={timelineSelection}
        onDisclosureChanged={revalidate}
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
  return { timeline, recordDetails };
};
const CaseDisclosure = ({
  target,
  onBack,
  onAct,
  onMutationLockChange = ignoreMutationLock,
  unavailableAct,
  reloadSignal,
  disclosureAvailable = true,
  authoringAvailable = disclosureAvailable,
}: CaseDetailProps) => {
  const current = useCaseDisclosure(target);
  useCaseRefresh(reloadSignal, disclosureAvailable, current.loading, current.revalidate);
  const retainedNoteOffer = useRetainedNoteOffer(current.value, current.denied);
  const { historyAccess } = current;
  const timelineSelection = useTimelineSelection(historyAccess);
  const state = disclosureAvailable ? current.value : null;
  const lookup = asCurrentCase(state);
  const mounted = useRetainedCase(lookup);
  const { timeline, recordDetails } = caseReadPresentation(
    state,
    disclosureAvailable,
    historyAccess,
    target,
    timelineSelection,
    current.revalidate,
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
          authoringAvailable={authoringAvailable && retainedNoteOffer}
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
    <CaseDisclosure key={JSON.stringify([state.epoch, state.token, props.target])} {...props} />
  ) : null;
};
