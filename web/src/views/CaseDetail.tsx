import type { CaseSelector, CaseView } from "@contracts/client";
import { useRequesterContext } from "../hooks/useRequesterContext";
import { MaintenanceNotice } from "../components/MaintenanceNotice";
import { useState } from "react";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import type { Notice } from "../protocol/notices";
import { Button } from "react-aria-components/Button";
import type { CurrentCase, CatalogDocument } from "../api/service";
import { isCurrentCase } from "../api/service";
import { CaseActivityDisclosure } from "./work/CaseActivity";
import { CaseReminders } from "./work/CaseReminders";
import { CaseFacts } from "./case/CaseFacts";
import { CaseActions, CasePrimaryAction } from "./case/CaseActions";
import { CaseProgress } from "./case/CaseProgress";
import { CharacterWarning } from "../components/BusinessValue";
import "./case.css";
import { CopyValue } from "../components/CopyValue";
import { type ActKind } from "../operation/metadata";
import { useCaseDisclosure } from "../hooks/useCaseDisclosure";
import { CaseHistory } from "./CaseHistory";
import { LifecycleStatus } from "./LifecycleStatus";
import { CaseStateBadge } from "../components/CaseStateBadge";

type CaseDetailProps = {
  target: CaseSelector;
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
  onAct,
  onChanged,
  onMutationLockChange,
  disclosureAvailable,
}: {
  current: CurrentCase;
  definition: CatalogDocument;
  onAct: (current: CurrentCase, act: ActKind) => void;
  onChanged: () => void;
  disclosureAvailable: boolean;
  onMutationLockChange: (locked: boolean) => void;
}) => {
  const globalPaused = useRequesterContext()?.maintenance.active === true;
  return (
    <>
      {disclosureAvailable ? (
        <>
          <MaintenanceNotice active={current.advice.maintenance.active && !globalPaused} />
          <p className="case-claimant">
            <bdi>{current.state.fields.registration.claimantName}</bdi>
            <CharacterWarning value={current.state.fields.registration.claimantName} />
          </p>
          <CaseProgress current={current} definition={definition} />
        </>
      ) : null}
      <CaseReminders
        current={current}
        onChanged={onChanged}
        onLockChange={onMutationLockChange}
        disclosureAvailable={disclosureAvailable}
      />
      {disclosureAvailable ? (
        <div className="case-workspace">
          <CaseFacts
            values={current.state.fields}
            revision={current.revision}
            fields={definition.fields}
          />
          <CaseActions
            current={current}
            onAct={(act) => {
              onAct(current, act);
            }}
          />
        </div>
      ) : null}
    </>
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
  const label = p.fieldLabel("caseReference");
  return (
    <>
      <div className="case-back">
        <Button onPress={onBack}>{p.text("ui.backToCases")}</Button>
      </div>
      <div className="section-heading case-heading">
        <div>
          <p className="eyebrow">{p.text("ui.caseWorkspace")}</p>
          <h1 id="case-detail-title" tabIndex={-1} aria-label={p.text("ui.caseDetail")}>
            <bdi dir="ltr" className="identifier-value">
              {caseReference}
            </bdi>
            <CharacterWarning value={caseReference} />
          </h1>
          {state === null ? null : (
            <div className="case-header-state">
              <CaseStateBadge
                state={
                  state.state.kind === "ERASED"
                    ? state.state
                    : {
                        kind: state.state.kind,
                        status: state.state.fields.status,
                        progress: state.state.fields.progress,
                      }
                }
              />
              <span>{p.text("ui.revision", { revision: p.integer(state.revision) })}</span>
              {state.mine ? <span className="assigned-marker">{p.text("ui.mine")}</span> : null}
            </div>
          )}
        </div>
        {state !== null && isCurrentCase(state) ? (
          <CasePrimaryAction
            current={state}
            onAct={(act) => {
              onAct(state, act);
            }}
          />
        ) : null}
      </div>
      <p>
        <CopyValue icon label={label} value={caseReference} />
      </p>
      {state === null ? null : (
        <details className="case-technical">
          <summary>{p.text("ui.technicalDetails")}</summary>
          <dl>
            <div>
              <dt>{p.fieldLabel("caseId")}</dt>
              <dd>
                <bdi dir="ltr" className="identifier-value">
                  {state.caseId}
                </bdi>{" "}
                <CopyValue label={p.fieldLabel("caseId")} value={state.caseId} />
              </dd>
            </div>
            <div>
              <dt>{p.text("label.revisionLabel")}</dt>
              <dd>{p.integer(state.revision)}</dd>
            </div>
          </dl>
        </details>
      )}
    </>
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
const CaseDisclosure = ({
  target,
  definition,
  onBack,
  onAct,
  onMutationLockChange = ignoreMutationLock,
}: CaseDetailProps) => {
  const p = usePresentation();
  const current = useCaseDisclosure(target);
  const state = current.value;
  const lookup = state !== null && isCurrentCase(state) ? state : null;
  const mounted = useRetainedCase(lookup);
  return (
    <section className="case-detail" aria-labelledby="case-detail-title">
      <CaseHeader target={target} onBack={onBack} state={state} onAct={onAct} />
      <CurrentFeedback message={current.message} loading={current.loading} />
      {state === null ? null : <LifecycleStatus value={state} />}
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
          disclosureAvailable={lookup !== null}
          definition={definition}
          onAct={onAct}
          onChanged={current.revalidate}
          onMutationLockChange={onMutationLockChange}
        />
      )}
      {state?.state.kind === "ERASED" ? (
        <p role="status">
          <bdi dir="ltr">{state.caseReference}</bdi> ·{" "}
          <bdi dir="ltr">{p.integer(state.revision)}</bdi>
        </p>
      ) : null}
      {current.historyModes.length ? (
        <CaseActivityDisclosure
          target={target}
          modes={current.historyModes}
          onDisclosureRefused={current.revalidate}
        />
      ) : null}
      {current.historyModes.length ? (
        <CaseHistory
          target={target}
          fields={definition.fields}
          modes={current.historyModes}
          onDisclosureRefused={current.revalidate}
        />
      ) : null}
    </section>
  );
};

export const CaseDetail = (props: CaseDetailProps) => (
  <CaseDisclosure key={JSON.stringify([props.target, props.reloadSignal])} {...props} />
);
