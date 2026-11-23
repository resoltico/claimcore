import { apiEndpoints } from "../../api/service";
import { isOperationAffordance } from "../../operation/affordances";
import { operationTask } from "../../protocol/taskCatalog";
import type { CurrentCase } from "../../protocol/types";
import { PresentedValue } from "../../components/PresentedValue";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { coreLinkPath } from "../../routing/coreLinks";
import { followLink } from "../../routing/followLink";
import { navigate } from "../../routing/navigation";
import { Chip } from "../../ui/Chip";
import { Button } from "../../ui/Button";
import { ActionMenu } from "../../ui/ActionMenu";
import { Section } from "../../ui/Section";
import { EmptyState } from "../../ui/EmptyState";
import type { ReminderActHandler } from "./reminderActions";
import "./case-next-actions.css";

export const CaseNextActions = ({
  current,
  canCreate,
  onCreate,
  onReminder,
}: {
  current: CurrentCase;
  canCreate: boolean;
  onCreate: () => void;
  onReminder: ReminderActHandler;
}) => {
  const p = usePresentation();
  const region = current.screen.regions.find((item) => item.role === "NEXT_ACTIONS");
  if (region?.content.kind !== "TABLE") {
    return null;
  }
  const table = region.content;
  const zone = current.basis.observation.timeZone;
  const actionIndex = table.columns.findIndex(({ key }) => key === "action");
  const dueIndex = table.columns.findIndex(({ key }) => key === "due");
  const kindIndex = table.columns.findIndex(({ key }) => key === "kind");
  const all = current.links.find(({ link }) => link.endpoint === apiEndpoints.reminderList);
  const allPath = all === undefined ? undefined : coreLinkPath(all.link);
  const create = canCreate ? (
    <Button id="case-reminder-add" variant="quiet" compact onPress={onCreate}>
      {p.text("task.reminder.create.label")}
    </Button>
  ) : undefined;
  return (
    <div className="case-reminders" data-region="NEXT_ACTIONS">
      <Section
        id="case-reminders-title"
        title={renderCatalogKey(p, region.titleId ?? "label.nextActions")}
        actions={create}
      >
        {table.rows.length === 0 && region.emptyId !== undefined ? (
          <EmptyState messageId={region.emptyId} />
        ) : (
          <ul className="next-action-cards">
            {table.rows.map((row) => {
              const message = row.values[actionIndex];
              const due = row.values[dueIndex];
              const kind = row.values[kindIndex];
              const controls = (row.actions ?? []).filter(isOperationAffordance);
              const recommended = controls.find((action) => action.task === row.recommended);
              const actionId = `reminder-${row.key}-actions`;
              return (
                <li key={row.key}>
                  {message === undefined ? null : (
                    <strong>
                      <PresentedValue value={message} timeZone={zone} />
                    </strong>
                  )}
                  {due === undefined ? null : <PresentedValue value={due} timeZone={zone} />}
                  {kind?.kind === "CODE" ? <Chip value={kind} /> : null}
                  <div className="next-action-controls">
                    {recommended === undefined ? null : (
                      <Button
                        compact
                        onPress={() => {
                          onReminder(recommended, actionId, true);
                        }}
                      >
                        {renderCatalogKey(p, recommended.labelId)}
                      </Button>
                    )}
                    {controls.length === 0 ? null : (
                      <ActionMenu
                        id={actionId}
                        icon
                        label={p.text("ui.actionsFor", {
                          row:
                            message?.kind === "TEXT" ? message.value : p.text("label.nextActions"),
                        })}
                        items={controls.map((action) => ({
                          id: action.task,
                          label: renderCatalogKey(
                            p,
                            operationTask(action.task).reviewedLabelId ?? action.labelId,
                          ),
                          disabled: action.blocked !== null,
                          select: () => {
                            onReminder(action, actionId);
                          },
                        }))}
                      />
                    )}
                  </div>
                </li>
              );
            })}
          </ul>
        )}
        {all === undefined || allPath === undefined ? null : (
          <a
            href={allPath}
            onClick={(event) => {
              followLink(event, () => {
                navigate(allPath);
              });
            }}
          >
            {renderCatalogKey(p, all.labelId)}
          </a>
        )}
      </Section>
    </div>
  );
};
