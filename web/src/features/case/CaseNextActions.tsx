import type { Region, RegionRow, Link } from "@contracts/client";
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
import { SuppliedMessage } from "../../ui/SuppliedMessage";
import { Section } from "../../ui/Section";
import { EmptyState } from "../../ui/EmptyState";
import type { ReminderActHandler } from "../work/reminderActions";
import "./case-next-actions.css";

type Table = Extract<Region["content"], { kind: "TABLE" }>;

const recommendationName = (p: ReturnType<typeof usePresentation>, labelId: string, row: string) =>
  p.text("ui.rowActionLabel", { action: renderCatalogKey(p, labelId), row });

const reminderCaption = (
  p: ReturnType<typeof usePresentation>,
  message: RegionRow["values"][number] | undefined,
) => (message?.kind === "TEXT" ? message.value : p.text("label.nextActions"));

const ReminderCard = ({
  row,
  table,
  zone,
  onReminder,
}: {
  row: RegionRow;
  table: Table;
  zone: string;
  onReminder: ReminderActHandler;
}) => {
  const p = usePresentation();
  const value = (key: string) =>
    row.values[table.columns.findIndex((column) => column.key === key)];
  const message = value("action");
  const due = value("due");
  const kind = value("kind");
  const timing = value("timing");
  const controls = (row.actions ?? []).filter(isOperationAffordance);
  const recommended = controls.find((action) => action.task === row.recommended);
  const rowLabel = reminderCaption(p, message);
  const actionId = `reminder-${row.key}-actions`;
  const primaryId = `reminder-${row.key}-primary`;
  return (
    <li>
      {message === undefined ? null : (
        <strong>
          <PresentedValue value={message} timeZone={zone} />
        </strong>
      )}
      <div className="next-action-meta">
        {due === undefined ? null : <PresentedValue value={due} timeZone={zone} />}
        {kind?.kind === "CODE" ? <Chip value={kind} /> : null}
        {timing?.kind === "CODE" ? (
          <span data-column="timing">
            <Chip value={timing} />
          </span>
        ) : null}
      </div>
      <div className="next-action-controls">
        {recommended === undefined ? null : (
          <Button
            id={primaryId}
            variant="secondary"
            compact
            isDisabled={recommended.blocked !== null}
            aria-label={recommendationName(p, recommended.labelId, rowLabel)}
            onPress={() => {
              onReminder(recommended, primaryId, true);
            }}
          >
            {renderCatalogKey(p, recommended.shortLabelId ?? recommended.labelId)}
          </Button>
        )}
        {controls.length === 0 ? null : (
          <ActionMenu
            id={actionId}
            icon
            label={p.text("ui.actionsFor", {
              row: rowLabel,
            })}
            items={controls.map((action) => ({
              id: action.task,
              label: renderCatalogKey(
                p,
                operationTask(action.task).reviewedLabelId ?? action.labelId,
              ),
              disabled: action.blocked !== null,
              description:
                action.blocked === null ? null : (
                  <SuppliedMessage message={action.blocked.message} timeZone={zone} />
                ),
              select: () => {
                onReminder(action, actionId);
              },
            }))}
          />
        )}
      </div>
    </li>
  );
};

const ReminderCards = ({
  region,
  zone,
  onReminder,
}: {
  region: Region | undefined;
  zone: string;
  onReminder: ReminderActHandler;
}) => {
  if (region?.content.kind !== "TABLE") {
    return null;
  }
  const { content } = region;
  if (content.rows.length === 0 && region.emptyId !== undefined) {
    return <EmptyState messageId={region.emptyId} />;
  }
  return (
    <ul className="next-action-cards">
      {content.rows.map((row) => (
        <ReminderCard key={row.key} row={row} table={content} zone={zone} onReminder={onReminder} />
      ))}
    </ul>
  );
};

const ReminderLink = ({ labelId, link }: { labelId: string; link: Link }) => {
  const p = usePresentation();
  const href = coreLinkPath(link);
  return href === undefined ? null : (
    <a
      href={href}
      onClick={(event) => {
        followLink(event, () => {
          navigate(href);
        });
      }}
    >
      {renderCatalogKey(p, labelId)}
    </a>
  );
};

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
  const all = current.links.find(({ link }) => link.endpoint === apiEndpoints.reminderList);
  if (region === undefined && all === undefined) {
    return null;
  }
  const create = canCreate ? (
    <Button id="case-reminder-add" variant="secondary" compact onPress={onCreate}>
      {p.text("task.reminder.create.label")}
    </Button>
  ) : undefined;
  return (
    <div className="case-reminders" data-region="NEXT_ACTIONS">
      <Section
        id="case-reminders-title"
        title={renderCatalogKey(p, region?.titleId ?? "label.nextActions")}
        actions={create}
      >
        <ReminderCards
          region={region}
          zone={current.basis.observation.timeZone}
          onReminder={onReminder}
        />
        {all === undefined ? null : <ReminderLink {...all} />}
      </Section>
    </div>
  );
};
