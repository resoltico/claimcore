import { useDocumentTitle } from "../../hooks/useDocumentTitle";
import { IdentifierValue } from "../../components/IdentifierValue";
import { Page, PageTitle } from "../../ui/Page";
import { SubjectHeader } from "../../ui/SubjectHeader";
import { useContentRoute } from "../../hooks/SettingsDetourContext";
import { followLink } from "../../routing/followLink";
import { isNarrativeCreationId, type ActKind } from "../../operation/metadata";
import type { Affordance, CaseSelector, HumanDocument, Screen } from "@contracts/client";
import type { MutationLockChange } from "../../hooks/useWorkspaceMutationLock";

import { useState, useLayoutEffect, useRef, type ReactNode } from "react";
import { useSessionContext } from "../../session/SessionContext";
import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";
import type { Notice } from "../../protocol/notices";
import { Button } from "../../ui/Button";
import { type CurrentCase, type CatalogDocument, isCurrentCase } from "../../api/service";

import { useCaseWorkPanels } from "./useCaseWorkPanels";
import { ScreenView } from "../../ui/ScreenView";
import { lastWorklist } from "../../routing/lastWorklist";
import { CaseActions } from "./CaseActions";
import "./case.css";

import { useCaseDisclosure } from "../../hooks/useCaseDisclosure";
import { CaseFactRegions } from "./CaseFactRegions";
import { CaseTitleStrip } from "./CaseTitleStrip";
import { RegionView } from "../../ui/regions/RegionView";
import { CaseTimeline, type TimelineFilter } from "./CaseTimeline";

type CaseDetailProps = {
  target: CaseSelector;
  operation?:
    | {
        key: string;
        act: ActKind;
        onClose?: () => void;
        render: (
          current: CurrentCase,
          revalidate: () => void,
          onUnavailable: () => void,
          available: boolean,
          onObserved: (value: CurrentCase) => void,
          currentForEntry: boolean,
        ) => ReactNode;
      }
    | undefined;
  reloadSignal: number;
  disclosureAvailable?: boolean;
  authoringAvailable?: boolean;
  definition: CatalogDocument;
  onBack: (worklist?: string) => void;
  onFind?: ((invoker: string) => void) | undefined;
  onMutationLockChange?: MutationLockChange;
  onAct: (current: CurrentCase, action: Affordance, origin?: string) => void;
};

const referenceFindId = "case-reference-find";

const CurrentFeedback = ({
  message,
  loading,
  onRetry,
  onFind,
}: {
  message: Notice | null;
  loading: boolean;
  onRetry: () => void;
  onFind: CaseDetailProps["onFind"];
}) => {
  const p = usePresentation();
  const correction = message?.kind === "humanIssues";
  const retry =
    message !== null &&
    !correction &&
    (message.kind !== "human" ||
      ["RETRY_SAFE", "READ_CURRENT", "REVIEW_LATEST"].includes(message.advice.action));
  return (
    <>
      {message === null ? null : (
        <p className="error" role="alert">
          <NoticeView value={message} />
        </p>
      )}
      {correction && onFind !== undefined ? (
        <Button
          id={referenceFindId}
          variant="secondary"
          onPress={() => {
            onFind(referenceFindId);
          }}
        >
          {p.text("ui.findCase")}
        </Button>
      ) : null}
      {retry ? (
        <Button variant="secondary" onPress={onRetry}>
          {p.text("ui.tryAgain")}
        </Button>
      ) : null}
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
  attachmentAuthoringAvailable,
  timeline,
  onAct,
}: {
  timeline: ReactNode;
  current: CurrentCase;
  view: Screen | null;
  onChanged: () => void;
  disclosureAvailable: boolean;
  authoringAvailable: boolean;
  attachmentAuthoringAvailable: boolean;
  onMutationLockChange: MutationLockChange;
  onAct: CaseDetailProps["onAct"];
}) => {
  const panels = useCaseWorkPanels({
    current,
    onChanged,
    onLockChange: onMutationLockChange,
    disclosureAvailable,
    authoringAvailable,
    attachmentAuthoringAvailable,
  });
  const facts =
    disclosureAvailable && view !== null ? (
      <CaseFactRegions
        current={current}
        view={view}
        onChanged={onChanged}
        onAct={(action, origin) => {
          onAct(current, action, origin);
        }}
      />
    ) : null;
  return (
    <>
      <div className="case-workspace">
        <div className="case-work-panels">
          <div className="case-area-next">{panels.nextActions}</div>
          <div className="case-area-work">{panels.work}</div>
        </div>
        <div className="case-record-panels">
          {facts}
          <div className="case-area-notes">{panels.notes}</div>
          <div className="case-area-attachments">{panels.attachments}</div>
        </div>
        {view === null || !disclosureAvailable ? null : (
          <div className="case-area-related">
            <ScreenView
              view={view}
              roles={["RELATED_CASES"]}
              timeZone={current.basis.observation.timeZone}
              actionsDisabled={!authoringAvailable || panels.editing}
              onAction={(action, _row, origin) => {
                panels.onLinkAction(action, origin);
              }}
            />
          </div>
        )}
        {timeline === null ? null : <div className="case-area-activity">{timeline}</div>}
      </div>
      {panels.operation}
    </>
  );
};

const asCurrentCase = (state: HumanDocument | null) =>
  state !== null && isCurrentCase(state) ? state : null;
const caseReference = (identity: Screen["regions"][number] | undefined): string | undefined => {
  const entry =
    identity?.content.kind === "ENTRIES"
      ? identity.content.entries.find((candidate) => candidate.value.kind === "REFERENCE")
      : undefined;
  return entry?.value.kind === "REFERENCE" ? entry.value.value : undefined;
};

const caseTimeZone = (state: HumanDocument | null) =>
  state?.basis.kind === "SNAPSHOT" ? state.basis.observation.timeZone : "UTC";

const CloseReminderSelection = ({ onBack }: Pick<CaseDetailProps, "onBack">) => {
  const p = usePresentation();
  const route = useContentRoute();
  if (route.screen !== "case" || route.reminderId === undefined) {
    return null;
  }
  return (
    <Button
      variant="secondary"
      compact
      onPress={() => {
        onBack();
      }}
    >
      {p.text("ui.close")}
    </Button>
  );
};

const CaseHeader = ({
  target,
  state,
  onBack,
  onAct,
  operation,
}: Pick<CaseDetailProps, "target" | "onAct" | "onBack" | "operation"> & {
  state: HumanDocument | null;
}) => {
  const p = usePresentation();
  const back = lastWorklist();
  const current = asCurrentCase(state);
  const identity = state?.screen.regions.find((region) => region.role === "IDENTITY");
  const reference = caseReference(identity);
  const fallback = target.kind === "REFERENCE" ? target.caseReference : undefined;
  const breadcrumb = fallback ?? reference;
  const caseTitle = reference ?? breadcrumb ?? p.text("label.caseDetail");
  const timeZone = caseTimeZone(state);
  useDocumentTitle(
    operation === undefined ? caseTitle : `${p.actLabel(operation.act)} · ${caseTitle}`,
  );
  return (
    <div className="case-page-header">
      <nav className="case-breadcrumb" aria-label={p.text("ui.breadcrumb")}>
        <a
          href={back}
          onClick={(event) => {
            followLink(event, () => {
              onBack(back);
            });
          }}
        >
          {p.text("label.cases")}
        </a>
        {breadcrumb === undefined ? null : (
          <>
            <span aria-hidden="true"> › </span>
            <IdentifierValue value={breadcrumb} />
          </>
        )}
      </nav>
      <CloseReminderSelection onBack={onBack} />
      <section className="case-header-card">
        <SubjectHeader
          region={identity}
          fallback={fallback}
          timeZone={timeZone}
          renderTitleLine={(line) => <CaseTitleStrip>{line}</CaseTitleStrip>}
          renderTitle={(heading, text) => (
            <PageTitle id="case-detail-title" title={text ?? p.text("label.caseDetail")}>
              {heading}
            </PageTitle>
          )}
          actions={
            current === null ? undefined : (
              <CaseActions
                current={current}
                phonePrimary
                onAct={(act, origin) => {
                  onAct(current, act, origin);
                }}
              />
            )
          }
        />
        {state === null ? null : (
          <ScreenView
            embedded
            view={state.screen}
            roles={["PROGRESS"]}
            timeZone={caseTimeZone(state)}
          />
        )}
      </section>
    </div>
  );
};

const UnavailableAct = ({
  value,
  state,
  onAct,
  onClose,
}: {
  value: ActKind;
  state: HumanDocument;
  onAct: CaseDetailProps["onAct"];
  onClose: (() => void) | undefined;
}) => {
  const p = usePresentation();
  const current = asCurrentCase(state);
  return (
    <aside className="case-action-refusal" role="alert">
      <p>
        <strong>{p.actLabel(value)}</strong>: {p.text("label.actionNotOffered")}
      </p>
      {onClose === undefined ? null : (
        <button type="button" onClick={onClose}>
          {p.text("ui.returnToCase")}
        </button>
      )}
      {current === null ? null : (
        <CaseActions
          hideRecommendation
          current={current}
          onAct={(act, origin) => {
            onAct(current, act, origin);
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
  return timeline;
};
type OperationObservation = {
  key: string;
  document: CurrentCase;
  workspaceBasis: HumanDocument | null;
};
const pageObservation = (
  current: ReturnType<typeof useCaseDisclosure>,
  disclosureAvailable: boolean,
  operation: CaseDetailProps["operation"],
  updated: OperationObservation | null,
) => {
  if (!disclosureAvailable) {
    return null;
  }
  if (
    updated !== null &&
    updated.key === operation?.key &&
    updated.workspaceBasis === current.value &&
    current.value?.disclosure === "CURRENT"
  ) {
    return updated.document;
  }
  return current.value;
};

const OperationBanner = ({
  state,
  operation,
  act,
  onAct,
}: {
  state: HumanDocument | null;
  operation: CaseDetailProps["operation"];
  act: ActKind | null;
  onAct: CaseDetailProps["onAct"];
}) =>
  state === null || act === null ? null : (
    <UnavailableAct value={act} state={state} onAct={onAct} onClose={operation?.onClose} />
  );

const OperationLayer = ({
  operation,
  mounted,
  availability,
  currentForEntry,
  revalidate,
  onUnavailable,
  onObserved,
}: {
  operation: CaseDetailProps["operation"];
  mounted: CurrentCase | null;
  availability: boolean;
  currentForEntry: boolean;
  revalidate: () => void;
  onUnavailable: (key: string) => void;
  onObserved: (observation: { key: string; document: CurrentCase }) => void;
}) => {
  if (operation === undefined || mounted === null) {
    return null;
  }
  return operation.render(
    mounted,
    revalidate,
    () => {
      onUnavailable(operation.key);
    },
    availability,
    (document) => {
      onObserved({ key: operation.key, document });
    },
    currentForEntry,
  );
};

const useCaseOperationObservation = (operation: CaseDetailProps["operation"]) => {
  const [unavailable, setUnavailable] = useState<string | null>(null);
  const [updated, setUpdated] = useState<OperationObservation | null>(null);
  if (updated !== null && updated.key !== operation?.key) {
    setUpdated(null);
  }
  if (unavailable !== null && unavailable !== operation?.key) {
    setUnavailable(null);
  }
  return {
    unavailableAct: unavailable === operation?.key ? operation.act : null,
    updated,
    reportUnavailable: setUnavailable,
    observe: setUpdated,
  };
};

const CaseNotices = ({ state }: { state: HumanDocument | null }) => {
  if (state === null) {
    return null;
  }
  return state.screen.regions
    .filter((region) => region.role === "NOTICE")
    .map((region) => (
      <RegionView key={region.role} region={region} timeZone={caseTimeZone(state)} />
    ));
};

const operationIsAvailable = (
  available: boolean,
  current: ReturnType<typeof useCaseDisclosure>,
  lookup: CurrentCase | null,
) => available && !current.denied && (lookup !== null || current.loading);

const CaseDisclosure = ({
  target,
  onBack,
  onFind,
  onAct,
  onMutationLockChange = ignoreMutationLock,
  operation,
  reloadSignal,
  disclosureAvailable = true,
  authoringAvailable = disclosureAvailable,
}: CaseDetailProps) => {
  const current = useCaseDisclosure(target);
  const { unavailableAct, updated, reportUnavailable, observe } =
    useCaseOperationObservation(operation);
  useCaseRefresh(reloadSignal, disclosureAvailable, current.loading, current.revalidate);
  const retainedNoteOffer = useRetainedNoteOffer(current.value, current.denied);
  const state = pageObservation(current, disclosureAvailable, operation, updated);
  const historyAccess = state?.history ?? current.historyAccess;
  const timelineSelection = useTimelineSelection(historyAccess);
  const lookup = asCurrentCase(state);
  const mounted = useRetainedCase(lookup);
  const timeline = caseReadPresentation(
    state,
    disclosureAvailable,
    historyAccess,
    target,
    timelineSelection,
    current.revalidate,
  );
  return (
    <Page
      as="section"
      labelledBy="case-detail-title"
      className="case-detail"
      header={
        <CaseHeader
          target={target}
          state={state}
          onAct={onAct}
          onBack={onBack}
          operation={operation}
        />
      }
      banner={
        <OperationBanner state={state} operation={operation} act={unavailableAct} onAct={onAct} />
      }
    >
      <CurrentFeedback
        message={current.message}
        loading={current.loading}
        onFind={onFind}
        onRetry={() => {
          current.revalidate();
          document.getElementById("case-detail-title")?.focus();
        }}
      />
      <CaseNotices state={state} />
      {mounted === null ? (
        timeline
      ) : (
        <CurrentPresentation
          current={mounted}
          timeline={timeline}
          disclosureAvailable={lookup !== null}
          authoringAvailable={authoringAvailable && retainedNoteOffer}
          attachmentAuthoringAvailable={authoringAvailable}
          view={state?.screen ?? null}
          onChanged={current.revalidate}
          onMutationLockChange={onMutationLockChange}
          onAct={onAct}
        />
      )}
      <OperationLayer
        operation={operation}
        mounted={mounted}
        revalidate={current.revalidate}
        onUnavailable={reportUnavailable}
        onObserved={(observation) => {
          observe({ ...observation, workspaceBasis: current.value });
        }}
        currentForEntry={lookup !== null && !current.loading && !current.denied}
        availability={operationIsAvailable(disclosureAvailable, current, lookup)}
      />
    </Page>
  );
};

export const CaseDetail = (props: CaseDetailProps) => {
  const { state } = useSessionContext();
  return state.kind === "authenticated" ? (
    <CaseDisclosure key={JSON.stringify([state.epoch, state.token, props.target])} {...props} />
  ) : null;
};
