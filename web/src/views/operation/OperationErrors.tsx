import type { OperationModel } from "../../hooks/operation/useOperation";
import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { refusalPath } from "../../operation/operationForm";
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
const unplacedIssues = (model: OperationModel, entry: ReturnType<typeof operationEntry>) => {
  const { notice } = model;
  if (
    notice?.kind !== "diagnostic" ||
    notice.diagnostic.code !== "INVALID_INPUT" ||
    !("issues" in notice.diagnostic)
  ) {
    return [];
  }
  return notice.diagnostic.issues.filter(
    (issue) => refusalPath(issue.field, entry, model.values) === null,
  );
};
export const OperationErrors = ({ model }: { model: OperationModel }) => {
  const p = usePresentation();
  if (model.notice === null) {
    return null;
  }
  const entry = operationEntry(model.entry);
  const errors = Object.entries(model.fieldErrors).flatMap(([path, notice]) => {
    const input = [...entry.inputs, ...entry.collections].find(
      (candidate) => candidate.path.join(".") === path,
    );
    const label = input === undefined ? path : renderCatalogKey(p, input.labelId);
    const notices =
      notice.kind === "issues"
        ? notice.issues.map((issue) => ({ kind: "issues" as const, issues: [issue] }))
        : [notice];
    return notices.map((issue, index) => ({ path, notice: issue, label, key: `${path}:${index}` }));
  });
  const unplaced = unplacedIssues(model, entry);
  return (
    <div role="alert" tabIndex={-1} data-error-summary>
      <NoticeView value={model.notice} />
      {errors.length === 0 && unplaced.length === 0 ? null : (
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
          {unplaced.map((issue) => (
            <li key={`unplaced:${issue.field}:${issue.code}`}>
              <NoticeView value={{ kind: "issues", issues: [issue] }} />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
};
