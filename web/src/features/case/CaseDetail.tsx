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
        render: (
          current: CurrentCase,
          revalidate: () => void,
          onUnavailable: () => void,
          available: boolean,
          onObserved: (value: CurrentCase) => void,
        ) => ReactNode;
      }
    | undefined;
  reloadSignal: number;
  disclosureAvailable?: boolean;
  authoringAvailable?: boolean;
  definition: CatalogDocument;
  onBack: (worklist?: string) => void;
  onMutationLockChange?: MutationLockChange;
  onAct: (current: CurrentCase, action: Affordance, origin?: string) => void;
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
        <Button variant="secondary" onPress={onRetry}>
          {p.text("ui.tryAgain")}
        </Button>
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
const pageObservation = (
  current: ReturnType<typeof useCaseDisclosure>,
  disclosureAvailable: boolean,
  operation: CaseDetailProps["operation"],
  updated: { key: string; document: CurrentCase } | null,
) => {
  if (!disclosureAvailable) {
    return null;
  }
  if (
    updated !== null &&
    updated.key === operation?.key &&
    current.value?.disclosure === "CURRENT"
  ) {
    return updated.document;
  }
  return current.value;
};

const OperationBanner = ({
  state,
  act,
  onAct,
}: {
  state: HumanDocument | null;
  act: ActKind | null;
  onAct: CaseDetailProps["onAct"];
}) =>
  state === null || act === null ? null : (
    <UnavailableAct value={act} state={state} onAct={onAct} />
  );

const OperationLayer = ({
  operation,
  mounted,
  availability,
  revalidate,
  onUnavailable,
  onObserved,
}: {
  operation: CaseDetailProps["operation"];
  mounted: CurrentCase | null;
  availability: boolean;
  revalidate: () => void;
  onUnavailable: (act: ActKind) => void;
  onObserved: (observation: { key: string; document: CurrentCase }) => void;
}) => {
  if (operation === undefined || mounted === null) {
    return null;
  }
  return operation.render(
    mounted,
    revalidate,
    () => {
      onUnavailable(operation.act);
    },
    availability,
    (document) => {
      onObserved({ key: operation.key, document });
    },
  );
};

const useCaseOperationObservation = (operation: CaseDetailProps["operation"]) => {
  const [unavailableAct, setUnavailableAct] = useState<ActKind | null>(null);
  const [updated, setUpdated] = useState<{ key: string; document: CurrentCase } | null>(null);
  if (updated !== null && updated.key !== operation?.key) {
    setUpdated(null);
  }
  if (unavailableAct !== null && unavailableAct !== operation?.act) {
    setUnavailableAct(null);
  }
  return { unavailableAct, updated, reportUnavailable: setUnavailableAct, observe: setUpdated };
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
      banner={<OperationBanner state={state} act={unavailableAct} onAct={onAct} />}
    >
      <CurrentFeedback
        message={current.message}
        loading={current.loading}
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
        onObserved={observe}
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
