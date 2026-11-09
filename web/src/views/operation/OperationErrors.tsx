import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { taskInput } from "../../protocol/taskCatalog";
import type { Notice } from "../../protocol/notices";
import type { HumanRejection } from "@contracts/client";

type Model = {
  notice: Notice | null;
  fieldErrors: Readonly<Record<string, Notice>>;
  rejection: HumanRejection | null;
};
const focusField = (id: string) => {
  const target = document.getElementById(`field-${id}`) ?? document.getElementsByName(id)[0];
  if (target instanceof HTMLElement) {
    const control = target.matches("input,textarea,button")
      ? target
      : target.querySelector<HTMLElement>(
          "input:not([type=hidden]),textarea,button,[role=spinbutton]",
        );
    (control ?? target).focus();
  }
};
export const OperationErrors = ({ model }: { model: Model }) => {
  const p = usePresentation();
  if (model.notice === null) {
    return null;
  }
  const issues = model.rejection?.kind === "INPUT" ? model.rejection.issues : [];
  return (
    <div role="alert" tabIndex={-1} data-error-summary>
      <NoticeView value={model.notice} />
      {issues.length === 0 ? null : (
        <ul>
          {issues.map((issue, index) => {
            const bound = Object.entries(model.fieldErrors).find(
              ([, notice]) => notice.kind === "humanIssues" && notice.issues.includes(issue),
            )?.[0];
            const message = <NoticeView value={{ kind: "humanIssues", issues: [issue] }} />;
            return (
              <li key={index}>
                {bound === undefined ? (
                  message
                ) : (
                  <a
                    href={`#field-${bound}`}
                    onClick={(event) => {
                      event.preventDefault();
                      focusField(bound);
                    }}
                  >
                    {renderCatalogKey(p, taskInput(issue.input).labelId)}: {message}
                  </a>
                )}
              </li>
            );
          })}
        </ul>
      )}
      {issues.length === 0
        ? Object.entries(model.fieldErrors).map(([id, notice]) => (
            <p key={id}>
              <a
                href={`#field-${id}`}
                onClick={(event) => {
                  event.preventDefault();
                  focusField(id);
                }}
              >
                <NoticeView value={notice} />
              </a>
            </p>
          ))
        : null}
    </div>
  );
};
