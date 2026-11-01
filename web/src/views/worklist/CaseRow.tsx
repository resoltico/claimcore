import type { CaseSelector } from "@contracts/client";
import type { CaseSummary } from "../../api/service";
import { CharacterWarning } from "../../components/BusinessValue";
import { usePresentation } from "../../presentation/context";
import { changedTime } from "../../presentation/changedTime";
import { casePath } from "../../routing/routes";
import { followLink } from "../../routing/followLink";
import { CaseStateBadge } from "../../components/CaseStateBadge";
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
      <td className="worklist-money">
        <Money live={live} />
      </td>
      <td className="worklist-badges">
        <CaseStateBadge state={state} />
      </td>
      <td className="worklist-work">
        <span className="worklist-cell-label">{p.term("section", "work")}</span>
        {live === null ? null : (
          <>
            {live.work.responsible === null ? (
              <span>{p.term("assignment", "UNASSIGNED")}</span>
            ) : (
              <>
                <bdi dir="auto">{live.work.responsible.account.subject}</bdi>
                <CharacterWarning value={live.work.responsible.account.subject} />
                {live.work.responsible.available ? null : (
                  <small>{p.text("label.assigneeUnavailable")}</small>
                )}
              </>
            )}
            <small>
              {live.work.followUpDate === null ? (
                p.term("due", "UNSCHEDULED")
              ) : (
                <time dateTime={live.work.followUpDate}>
                  <bdi dir="ltr">{p.date(live.work.followUpDate)}</bdi>
                </time>
              )}
              {" · "}
              {p.term("due", live.work.due)}
            </small>
          </>
        )}
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
