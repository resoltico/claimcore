import type { CaseSummary } from "../../api/service";
import { CharacterWarning } from "../../components/BusinessValue";
import { CopyValue } from "../../components/CopyValue";
import { usePresentation } from "../../presentation/context";
import { changedTime } from "../../presentation/changedTime";
import { casePath } from "../../routing/routes";
const Changed = ({ value }: { value: string }) => {
  const p = usePresentation();
  const time = changedTime(value, p.displayLocale);
  return (
    <time dateTime={value} title={time.absolute} aria-label={time.absolute}>
      {time.relative}
    </time>
  );
};
const CaseFacts = ({ value }: { value: CaseSummary }) => {
  const p = usePresentation();
  const { state } = value;
  if (state.kind === "ERASED") {
    return (
      <td colSpan={4}>
        <span className="badge">{p.term("lifecycle", state.kind)}</span>
        <span>{p.text("ui.revision", { revision: p.integer(value.revision) })}</span>
      </td>
    );
  }
  return (
    <>
      <td className="worklist-person">
        <bdi>{state.claimantName}</bdi>
        <CharacterWarning value={state.claimantName} />
        <small>
          <bdi>{state.insurerName}</bdi>
        </small>
        <CharacterWarning value={state.insurerName} />
      </td>
      <td className="worklist-money">
        <span className="compact-label">{p.fieldLabel("claimedAmount")}: </span>
        <bdi dir="ltr">
          {p.amount(state.claimed.amount)} {state.claimed.currency}
        </bdi>
      </td>
      <td className="worklist-money">
        {state.payable === null ? (
          p.text("ui.notRecorded")
        ) : (
          <bdi dir="ltr">
            {p.amount(state.payable.amount)} {state.payable.currency}
          </bdi>
        )}
      </td>
      <td className="worklist-badges">
        {state.kind === "ACTIVE" ? null : (
          <span className="badge">{p.term("lifecycle", state.kind)}</span>
        )}
        <span className="badge">{p.term("status", state.status)}</span>
        <span className="badge">{p.term("progress", state.progress)}</span>
      </td>
    </>
  );
};
export const CaseRow = ({
  value,
  onSelect,
}: {
  value: CaseSummary;
  onSelect: (reference: string) => void;
}) => {
  const p = usePresentation();
  return (
    <tr className={`worklist-row ${value.state.kind === "ERASED" ? "erased-row" : ""}`}>
      <td className="worklist-reference">
        <a
          className="reference"
          href={casePath(value.caseReference)}
          onClick={(event) => {
            event.preventDefault();
            onSelect(value.caseReference);
          }}
        >
          <bdi dir="ltr">{value.caseReference}</bdi>
        </a>
        <CharacterWarning value={value.caseReference} />
        <small>
          {p.text("ui.caseRow", {
            status: p.term("lifecycle", value.state.kind),
            revision: p.integer(value.revision),
          })}
        </small>
        <CopyValue label={p.fieldLabel("caseReference")} value={value.caseReference} />
      </td>
      <CaseFacts value={value} />
      <td>
        <Changed value={value.changedAt} />
      </td>
    </tr>
  );
};
