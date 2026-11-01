import type { CaseWorkView } from "@contracts/client";
import { IdentifierValue } from "../../components/IdentifierValue";
import { CharacterWarning } from "../../components/BusinessValue";
import { CopyValue } from "../../components/CopyValue";
import { usePresentation } from "../../presentation/context";

export const WorkAccount = ({ value }: { value: CaseWorkView["responsible"] }) => {
  const p = usePresentation();
  if (value === null) {
    return <span>{p.term("assignment", "UNASSIGNED")}</span>;
  }
  return (
    <>
      <bdi dir="auto">{value.account.subject}</bdi>
      <CharacterWarning value={value.account.subject} />
      <details>
        <summary>{p.text("ui.accountDetails")}</summary>
        <IdentifierValue value={value.account.actorId} />{" "}
        <CopyValue label={p.fieldLabel("actorId")} value={value.account.actorId} />
        <p>{p.text(value.available ? "label.assigneeAvailable" : "label.assigneeUnavailable")}</p>
      </details>
    </>
  );
};
export const CaseWork = ({ value, onUpdate }: { value: CaseWorkView; onUpdate?: () => void }) => {
  const p = usePresentation();
  return (
    <section className="case-work" aria-labelledby="case-work-title">
      <div className="section-heading">
        <h2 id="case-work-title">{p.term("section", "work")}</h2>
        {onUpdate === undefined ? null : (
          <button type="button" onClick={onUpdate}>
            {p.actLabel("UPDATE_WORK")}
          </button>
        )}
      </div>
      <dl>
        <div>
          <dt>{p.fieldLabel("responsibleActor")}</dt>
          <dd>
            <WorkAccount value={value.responsible} />
          </dd>
        </div>
        <div>
          <dt>{p.fieldLabel("followUpDate")}</dt>
          <dd>
            {value.followUpDate === null ? (
              p.text("ui.notRecorded")
            ) : (
              <>
                <bdi dir="ltr">{p.date(value.followUpDate)}</bdi>{" "}
                <CopyValue label={p.fieldLabel("followUpDate")} value={value.followUpDate} />
              </>
            )}
            <span className={`work-due work-due-${value.due.toLowerCase()}`}>
              {p.term("due", value.due)}
            </span>
          </dd>
        </div>
      </dl>
      <p className="observation-basis">
        {p.text("ui.workDateBasis", {
          date: p.date(value.basis.businessDate),
          zone: value.basis.timeZone,
        })}
      </p>
    </section>
  );
};
