import type { OperationModel } from "../../hooks/operation/useOperation";
import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { operationEntry } from "../../protocol/operationCatalog";
const focusField = (path: string): void => {
  const target = document.getElementById(`field-${path}`) ?? document.getElementsByName(path)[0];
  if (target instanceof HTMLElement) {
    const input = target.matches("input,select,textarea")
      ? target
      : target.querySelector<HTMLElement>("input:not([type=hidden]),[role=spinbutton]");
    (input ?? target).focus();
  }
};
export const OperationErrors = ({ model }: { model: OperationModel }) => {
  const p = usePresentation();
  if (model.notice === null) {
    return null;
  }
  const entry = operationEntry(model.entry);
  const errors = Object.entries(model.fieldErrors).flatMap(([path, notice]) => {
    const input = entry.inputs.find((candidate) => candidate.path.join(".") === path);
    const label = input === undefined ? path : renderCatalogKey(p, input.labelId);
    const notices =
      notice.kind === "issues"
        ? notice.issues.map((issue) => ({ kind: "issues" as const, issues: [issue] }))
        : [notice];
    return notices.map((issue, index) => ({ path, notice: issue, label, key: `${path}:${index}` }));
  });
  return (
    <div role="alert" tabIndex={-1} data-error-summary>
      <NoticeView value={model.notice} />
      {errors.length === 0 ? null : (
        <ul>
          {errors.map((error) => (
            <li key={error.key}>
              <a
                href={`#field-${error.path}`}
                onClick={(event) => {
                  event.preventDefault();
                  focusField(error.path);
                }}
              >
                {error.label}: <NoticeView value={error.notice} />
              </a>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
};
