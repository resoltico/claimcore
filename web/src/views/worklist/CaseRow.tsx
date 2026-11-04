import { ActorIdentity } from "../../components/ActorIdentity";
import type { CaseSelector } from "@contracts/client";
import type { CaseSummary } from "../../api/service";
import { CharacterWarning } from "../../components/BusinessValue";
import { usePresentation } from "../../presentation/context";
import { changedTime } from "../../presentation/changedTime";
import { casePath } from "../../routing/routes";
import { followLink } from "../../routing/followLink";
import { CaseHeadline } from "../../components/CaseHeadline";
import { CaseAttention } from "../../components/CaseAttention";
const RowReference = ({ value }: { value: string }) => {
  const tail = Math.min(9, Math.floor(value.length / 2));
  return (
    <bdi dir="ltr" className="row-reference-text">
      <span className="reference-prefix">{value.slice(0, value.length - tail)}</span>
      <span className="reference-suffix">{tail === 0 ? "" : value.slice(-tail)}</span>
    </bdi>
  );
};
const Changed = ({ value }: { value: string }) => {
  const p = usePresentation();
  const time = changedTime(value, p.displayLocale);
  return (
    <time dateTime={value} title={time.absolute} aria-label={time.absolute}>
      {time.relative}
    </time>
  );
};
type LiveSummary = Exclude<CaseSummary["state"], { kind: "ERASED" }>;
const Parties = ({ live }: { live: LiveSummary | null }) =>
  live === null ? null : (
    <span className="worklist-person">
      <bdi>{live.claimantName}</bdi>
      <CharacterWarning value={live.claimantName} />
      <small>
        <bdi>{live.insurerName}</bdi>
        <CharacterWarning value={live.insurerName} />
      </small>
    </span>
  );
const Money = ({ live }: { live: LiveSummary | null }) => {
  const p = usePresentation();
  if (live === null) {
    return null;
  }
  return (
    <>
      <span className="worklist-cell-label">{p.fieldLabel("claimedAmount")}</span>
      <bdi dir="ltr">{p.money(live.claimed.amount, live.claimed.currency)}</bdi>
      <small className="worklist-payable">
        <span>{p.fieldLabel("payableAmount")}: </span>
        {live.payable === null ? (
          p.text("ui.notRecorded")
        ) : (
          <bdi dir="ltr">{p.money(live.payable.amount, live.payable.currency)}</bdi>
        )}
      </small>
    </>
  );
};
export const CaseRow = ({
  value,
  onSelect,
  businessDate,
}: {
  value: CaseSummary;
  onSelect: (target: CaseSelector) => void;
  businessDate: string;
}) => {
  const p = usePresentation();
  const { state } = value;
  const live = state.kind === "ERASED" ? null : state;
  const age = p.age(live?.incidentNotificationDate ?? null, businessDate);
  return (
    <tr className={`worklist-row ${state.kind === "ERASED" ? "erased-row" : ""}`}>
      <td className="worklist-reference">
        <a
          className="reference row-link"
          id={`case-${value.caseId}`}
          href={casePath(value.caseId)}
          title={value.caseReference}
          aria-label={value.caseReference}
          onClick={(event) => {
            followLink(event, () => {
              onSelect({ kind: "ID", caseId: value.caseId });
            });
          }}
        >
          <RowReference value={value.caseReference} />
        </a>
        <Parties live={live} />
      </td>
      <td className="worklist-badges">
        <CaseHeadline value={value.headline} />
      </td>
      <td className="worklist-next-action">
        <span className="worklist-cell-label">{p.text("ui.reminderAction")}</span>
        {value.nextAction === null ? null : (
          <>
            <bdi>{value.nextAction.action}</bdi>
            <small>
              <time dateTime={value.nextAction.due}>{p.date(value.nextAction.due)}</time>
              {" · "}
              <span className={`work-due work-due-${value.nextAction.dueState.toLowerCase()}`}>
                {p.term("due", value.nextAction.dueState)}
              </span>
            </small>
          </>
        )}
        <CaseAttention values={value.attention} />
        {value.pendingReminders > 1 ? (
          <small>
            {p.text("label.morePendingReminders", { count: p.integer(value.pendingReminders - 1) })}
          </small>
        ) : null}
      </td>
      <td className="worklist-responsible">
        <span className="worklist-cell-label">{p.fieldLabel("responsibleActor")}</span>
        {value.responsible === null ? (
          p.term("assignment", "UNASSIGNED")
        ) : (
          <>
            <ActorIdentity actor={value.responsible} />
            {value.responsible.availability === "AVAILABLE" ? null : (
              <small>{p.text("label.assigneeUnavailable")}</small>
            )}
          </>
        )}
      </td>
      <td className="worklist-money">
        <Money live={live} />
      </td>
      <td className="worklist-changed">
        <span className="worklist-cell-label">{p.text("label.changedAt")}</span>
        <Changed value={value.changedAt} />
        <small>
          <span>{p.text("ui.age")}: </span>
          <span>{age}</span>
        </small>
      </td>
    </tr>
  );
};
