import type { CaseSelector, CaseView, CaseActRefusal } from "@contracts/client";
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
import { CaseFacts } from "./case/CaseFacts";
import { CaseActions, CasePrimaryAction, CaseActionAlternatives } from "./case/CaseActions";
import { CaseProgress } from "./case/CaseProgress";
import { CharacterWarning } from "../components/BusinessValue";
import "./case.css";
import { CopyValue } from "../components/CopyValue";
import { type ActKind } from "../operation/metadata";
import { useCaseDisclosure } from "../hooks/useCaseDisclosure";
import { CaseTimeline, type TimelineFilter } from "./case/CaseTimeline";
import { CaseHeadline } from "../components/CaseHeadline";
import { CaseAttention } from "../components/CaseAttention";
import { CaseSectionActions } from "./case/CaseSectionActions";

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
  definition,
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
  definition: CatalogDocument;
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
  const facts = disclosureAvailable ? (
    <CaseFacts
      values={current.state.fields}
      fields={definition.fields}
      actions={Object.fromEntries(
        (["registration", "decision", "payment"] as const).map((section) => [
          section,
          <CaseSectionActions
            key={section}
            current={current}
            section={section}
            onAct={(act) => {
              onAct(current, act);
            }}
          />,
        ]),
      )}
    />
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

const asCurrentCase = (state: CaseView | null) =>
  state !== null && isCurrentCase(state) ? state : null;
const CaseMeta = ({ current }: { current: CurrentCase }) => {
  const p = usePresentation();
  const { registration } = current.state.fields;
  return (
    <p className="case-meta">
      <bdi>{registration.insurerName}</bdi> · {p.fieldLabel("incidentDate")}:{" "}
      <time dateTime={registration.incidentDate}>{p.date(registration.incidentDate)}</time> ·{" "}
      <bdi dir="ltr">{registration.incidentCountry}</bdi> ·{" "}
      {p.fieldLabel("incidentNotificationDate")}:{" "}
      <time dateTime={registration.incidentNotificationDate}>
        {p.date(registration.incidentNotificationDate)}
      </time>{" "}
      · {p.age(registration.incidentNotificationDate, current.work.basis.businessDate)}
    </p>
  );
};
const CaseHeader = ({
  target,
  onBack,
  state,
  onAct,
}: Pick<CaseDetailProps, "target" | "onBack" | "onAct"> & { state: CaseView | null }) => {
  const p = usePresentation();
  const caseReference =
    state?.caseReference ??
    (target.kind === "REFERENCE" ? target.caseReference : p.text("ui.caseDetail"));
  const current = asCurrentCase(state);
  const claimant = current?.state.fields.registration.claimantName;
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
            <bdi>{claimant ?? p.text("ui.caseDetail")}</bdi>
            <CharacterWarning value={claimant ?? null} />
          </h1>
          {current === null ? null : <CaseMeta current={current} />}
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

const SummaryRecordDetails = ({ state }: { state: CaseView }) => {
  const p = usePresentation();
  const { summary, summaryChangedAt, summaryRecordedBy } = state.work;
  if (summary === null || summaryChangedAt === null || summaryRecordedBy === null) {
    return null;
  }
  return (
    <div>
      <dt>{p.fieldLabel("handlingSummary")}</dt>
      <dd>
        <p>
          {p.text("ui.exactUtc")}:{" "}
          <time dateTime={summaryChangedAt}>
            <bdi dir="ltr">{summaryChangedAt}</bdi>
          </time>
          <CopyValue icon label={p.text("ui.exactUtc")} value={summaryChangedAt} />
        </p>
        <p>
          {p.text("input.actorId.label")}: <bdi dir="ltr">{summaryRecordedBy.actorId}</bdi>
          <CopyValue icon label={p.text("input.actorId.label")} value={summaryRecordedBy.actorId} />
        </p>
      </dd>
    </div>
  );
};
const CaseRecordDetails = ({ state }: { state: CaseView }) => {
  const p = usePresentation();
  return (
    <details className="case-technical" data-record-details>
      <summary>{p.text("label.recordDetails")}</summary>
      <dl>
        <SummaryRecordDetails state={state} />
        <div>
          <dt>{p.fieldLabel("caseId")}</dt>
          <dd>
            <bdi dir="ltr" className="identifier-value">
              {state.caseId}
            </bdi>
            <CopyValue icon label={p.fieldLabel("caseId")} value={state.caseId} />
          </dd>
        </div>
        <div>
          <dt>{p.fieldLabel("caseReference")}</dt>
          <dd>
            <bdi dir="ltr">{state.caseReference}</bdi>
            <CharacterWarning value={state.caseReference} />
            <CopyValue icon label={p.fieldLabel("caseReference")} value={state.caseReference} />
          </dd>
        </div>
        {state.work.responsible === null ? null : (
          <div>
            <dt>{p.text("ui.accountDetails")}</dt>
            <dd>
              <bdi dir="ltr">{state.work.responsible.actorId}</bdi>
              <CopyValue
                icon
                label={p.fieldLabel("actorId")}
                value={state.work.responsible.actorId}
              />
            </dd>
          </div>
        )}
        <div>
          <dt>{p.text("label.revisionLabel")}</dt>
          <dd>{p.integer(state.revision)}</dd>
        </div>
        <div>
          <dt>{p.term("section", "work")}</dt>
          <dd>{p.integer(state.work.revision)}</dd>
        </div>
        <div>
          <dt>{p.text("ui.shell.businessDate")}</dt>
          <dd>
            <time dateTime={state.work.basis.businessDate}>
              {p.date(state.work.basis.businessDate)}
            </time>{" "}
            · <bdi>{state.work.basis.timeZone}</bdi>
          </dd>
        </div>
      </dl>
    </details>
  );
};

const UnavailableAct = ({
  value,
  state,
  onAct,
}: {
  value: CaseActRefusal;
  state: CaseView;
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
const CaseDisclosure = ({
  target,
  definition,
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
  const recordDetails = state === null ? null : <CaseRecordDetails state={state} />;
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
          definition={definition}
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
