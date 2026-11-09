import type { Screen } from "@contracts/client";
import type { RecoveryRecord } from "../../protocol/recoveryJournal";
import { casePath } from "../../routing/routes";
import { followLink } from "../../routing/followLink";
import { navigate } from "../../routing/navigation";
import { IdentifierValue } from "../../components/IdentifierValue";
import { CopyValue } from "../../components/CopyValue";
import { useRecovery, type RecoveryFinalAnswer } from "../../recovery/RecoveryContext";
import { useRequesterContext } from "../../hooks/useRequesterContext";
import { NoticeView } from "../../presentation/Message";
import { operationEntryForInput } from "../../protocol/operationCatalog";
import { renderCatalogKey } from "../../presentation/messages";
import { usePresentation } from "../../presentation/context";
import { ScreenView } from "../../ui/ScreenView";
import "./unconfirmed.css";

const stateLabels = {
  CHECKING: "ui.checking",
  RECORDED: "ui.stage.recorded",
  NOT_RECORDED: "ui.notRecorded",
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
    <details data-region="DETAILS">
      <summary>{p.text("label.recordDetails")}</summary>
      <dl>
        <dt>{p.fieldLabel("operationId")}</dt>
        <dd>
          <IdentifierValue value={operationId} />
          <CopyValue label={p.fieldLabel("operationId")} value={operationId} />
        </dd>
      </dl>
    </details>
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
  const state = answer?.outcome.kind ?? "CHECKING";
  const view = answer?.outcome.view ?? pending;
  const zone =
    answer?.outcome.kind === "RECORDED" ? answer.outcome.timeZone : (requester?.timeZone ?? "UTC");
  return (
    <li className="recovery-attempt">
      <div className="recovery-card-heading">
        <span className="recovery-state" data-state={state}>
          {p.text(stateLabels[state])}
        </span>
        {state === "RECORDED" ? null : (
          <strong>
            {renderCatalogKey(
              p,
              operationEntryForInput({ kind: record.subject, act: { kind: record.act } }).labelId,
            )}
          </strong>
        )}
      </div>
      <RecoveryContent view={view} timeZone={zone} notRecorded={state === "NOT_RECORDED"} />
      {state === "RECORDED" ? null : <p>{p.text("ui.recoveryIdentityOnly")}</p>}
      <RecoveryControls answer={answer} record={record} busy={busy} check={check} />
      <RecoveryIdentity operationId={record.operationId} view={view} />
    </li>
  );
};

export const UnresolvedNotice = ({ showEmpty = false }: { showEmpty?: boolean }) => {
  const p = usePresentation();
  const { operations, presentations, finals, busy, notice, check } = useRecovery();
  const records = [
    ...operations,
    ...Object.values(finals)
      .filter(
        (answer) => !operations.some((record) => record.attemptId === answer.record.attemptId),
      )
      .map((answer) => answer.record),
  ];
  if (!showEmpty && records.length === 0 && notice === null) {
    return null;
  }
  return (
    <section className="unconfirmed-list" role="status" aria-busy={busy !== null}>
      {showEmpty && records.length === 0 && notice === null ? (
        <p>{p.text("ui.recoveryEmpty")}</p>
      ) : null}
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
