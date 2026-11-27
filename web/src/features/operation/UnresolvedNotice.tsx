import { TechnicalDetails } from "../../ui/TechnicalDetails";
import type { Screen } from "@contracts/client";
import { recoveryTask, type RecoveryRecord } from "../../protocol/recoveryJournal";
import { casePath } from "../../routing/routes";
import { followLink } from "../../routing/followLink";
import { navigate } from "../../routing/navigation";
import { IdentifierValue } from "../../components/IdentifierValue";
import { CopyValue } from "../../components/CopyValue";
import { useRecovery, type RecoveryFinalAnswer } from "../../recovery/RecoveryContext";
import { useRequesterContext } from "../../hooks/useRequesterContext";
import { NoticeView } from "../../presentation/Message";
import { renderCatalogKey } from "../../presentation/messages";
import { usePresentation } from "../../presentation/context";
import { ScreenView } from "../../ui/ScreenView";
import "./unconfirmed.css";

const stateLabels = {
  CHECKING: "ui.checking",
  RECORDED: "label.recorded",
  NOT_RECORDED: "label.notRecorded",
} as const;

const RecoveryContent = ({
  view,
  timeZone,
  notRecorded,
}: {
  view: Screen | undefined | null;
  timeZone: string;
  notRecorded: boolean;
}) => {
  if (view !== undefined && view !== null && view.regions.length > 0) {
    return <ScreenView view={view} timeZone={timeZone} />;
  }
  return notRecorded ? (
    <p>
      <NoticeView value={{ kind: "local", reason: "notAccepted" }} />
    </p>
  ) : null;
};
const RecoveryIdentity = ({
  operationId,
  view,
}: {
  operationId: string;
  view: Screen | undefined | null;
}) => {
  const p = usePresentation();
  if (view?.regions.some((region) => region.role === "DETAILS") === true) {
    return null;
  }
  return (
    <TechnicalDetails>
      <dl>
        <dt>{p.fieldLabel("operationId")}</dt>
        <dd>
          <IdentifierValue value={operationId} />
          <CopyValue label={p.fieldLabel("operationId")} value={operationId} />
        </dd>
      </dl>
    </TechnicalDetails>
  );
};
const RecoveryControls = ({
  answer,
  record,
  busy,
  check,
}: {
  answer: RecoveryFinalAnswer | undefined;
  record: RecoveryRecord;
  busy: boolean;
  check: (record: RecoveryRecord) => void;
}) => {
  const p = usePresentation();
  const caseId = answer?.outcome.kind === "RECORDED" ? answer.outcome.caseId : null;
  const cleanup = answer?.cleanup ?? null;
  return (
    <>
      {caseId === null ? null : (
        <a
          href={casePath(caseId)}
          onClick={(event) => {
            followLink(event, () => {
              navigate(casePath(caseId));
            });
          }}
        >
          {p.text("ui.openCase")}
        </a>
      )}
      {answer !== undefined && cleanup === null ? null : (
        <button
          type="button"
          disabled={busy}
          onClick={() => {
            check(record);
          }}
        >
          {p.text("ui.checkUnresolvedOperation")}
        </button>
      )}
      {cleanup === null ? null : (
        <p role="alert">
          <NoticeView value={cleanup} />
        </p>
      )}
    </>
  );
};
const RecoveryCard = ({
  record,
  answer,
  pending,
  busy,
  check,
}: {
  record: RecoveryRecord;
  answer: RecoveryFinalAnswer | undefined;
  pending: Screen | undefined;
  busy: boolean;
  check: (record: RecoveryRecord) => void;
}) => {
  const p = usePresentation();
  const requester = useRequesterContext();
  const task = recoveryTask(record);
  const state = answer?.outcome.kind ?? "CHECKING";
  const view = answer?.outcome.view ?? pending;
  const zone =
    answer?.outcome.kind === "RECORDED"
      ? answer.outcome.timeZone
      : (requester?.basis.observation.timeZone ?? "UTC");
  return (
    <li className="recovery-attempt">
      <div className="recovery-card-heading">
        <span className="recovery-state" data-state={state}>
          {p.text(stateLabels[state])}
        </span>
        {state === "RECORDED" || task === undefined ? null : (
          <strong>{renderCatalogKey(p, task.labelId)}</strong>
        )}
      </div>
      <RecoveryContent view={view} timeZone={zone} notRecorded={state === "NOT_RECORDED"} />
      {state === "RECORDED" ? null : <p>{p.text("ui.recoveryIdentityOnly")}</p>}
      <RecoveryControls answer={answer} record={record} busy={busy} check={check} />
      <RecoveryIdentity operationId={record.operationId} view={view} />
    </li>
  );
};

const journalMessage = {
  earlierFormat: "ui.recoveryEarlierFormat",
  unavailable: "ui.recoveryListingUnavailable",
  damaged: "ui.recoveryDamagedRecord",
  unscoped: "ui.recoveryDamagedRecord",
} as const;

export const UnresolvedNotice = ({ showEmpty = false }: { showEmpty?: boolean }) => {
  const p = usePresentation();
  const { operations, presentations, finals, busy, notice, journalIssues, check } = useRecovery();
  const records = [
    ...operations,
    ...Object.values(finals)
      .filter(
        (answer) => !operations.some((record) => record.attemptId === answer.record.attemptId),
      )
      .map((answer) => answer.record),
  ];
  if (!showEmpty && records.length === 0 && notice === null && journalIssues.length === 0) {
    return null;
  }
  return (
    <section className="unconfirmed-list" role="status" aria-busy={busy !== null}>
      {showEmpty && records.length === 0 && notice === null && journalIssues.length === 0 ? (
        <p>{p.text("ui.recoveryEmpty")}</p>
      ) : null}
      {journalIssues.map((issue) => (
        <p role="alert" key={issue}>
          {p.text(journalMessage[issue])}
        </p>
      ))}
      <ul>
        {records.map((record) => (
          <RecoveryCard
            key={record.attemptId}
            record={record}
            answer={finals[record.attemptId]}
            pending={presentations[record.attemptId]}
            busy={busy !== null}
            check={check}
          />
        ))}
      </ul>
      {notice === null ? null : (
        <div className="recovery-feedback">
          <NoticeView value={notice} />
        </div>
      )}
    </section>
  );
};
