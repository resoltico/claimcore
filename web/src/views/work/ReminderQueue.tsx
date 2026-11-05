import { ViewTabs } from "../../ui/ViewTabs";
import { useEffect, useMemo, useState } from "react";
import { Select } from "../../ui/Select";
import { usePresentation } from "../../presentation/context";
import { isTerm } from "../../presentation/terms";
import { useContentRoute } from "../../hooks/SettingsDetourContext";
import { navigate } from "../../routing/navigation";
import { parseReminderQuery, remindersPath, type ReminderQuery } from "../../routing/reminderQuery";
import { useWorkspaceViews, WorkspaceViewsStatus } from "../WorkspaceViews";
import { ReminderQueueWork } from "./ReminderQueueWork";
const DefaultReminderView = ({
  query,
  view,
}: {
  query: ReminderQuery;
  view: NonNullable<ReminderQuery["view"]>;
}) => {
  useEffect(() => {
    navigate(remindersPath({ ...query, view }), true);
  }, [query, view]);
  const p = usePresentation();
  return <p role="status">{p.text("ui.loading")}</p>;
};
const ReminderSearch = ({
  query,
  change,
  draft,
  onDraft,
}: {
  query: ReminderQuery;
  change: (value: ReminderQuery) => void;
  draft: string;
  onDraft: (value: string) => void;
}) => {
  const p = usePresentation();
  return (
    <form
      role="search"
      aria-label={p.text("ui.reminders")}
      onSubmit={(event) => {
        event.preventDefault();
        const { text: _text, ...rest } = query;
        change(draft === "" ? rest : { ...rest, text: draft });
      }}
    >
      <label>
        {p.text("ui.search")}
        <input
          type="search"
          value={draft}
          onChange={(event) => {
            onDraft(event.target.value);
          }}
        />
      </label>
      <button type="submit">{p.text("ui.search")}</button>
    </form>
  );
};
type QueueProps = { onLockChange?: (locked: boolean) => void; disclosureAvailable?: boolean };
const noLockChange = () => undefined;
const ReadyReminderQueue = ({
  query,
  onLockChange = noLockChange,
  disclosureAvailable = true,
}: { query: ReminderQuery } & QueueProps) => {
  const p = usePresentation();
  const views = useWorkspaceViews();
  const source = query.text ?? "";
  const [draft, setDraft] = useState({ source, value: source });
  if (draft.source !== source) {
    setDraft({ source, value: source });
  }
  const change = (value: ReminderQuery) => {
    navigate(remindersPath(value), true);
  };
  return (
    <section className="reminder-queue" aria-labelledby="reminder-queue-title">
      <h1 id="reminder-queue-title" tabIndex={-1}>
        {p.text("ui.reminders")}
      </h1>
      <WorkspaceViewsStatus area="reminders" />
      <ViewTabs
        items={
          views.reminders.data?.items.map(({ view, count }) => ({
            id: view,
            label: p.term("reminderView", view),
            count: p.integer(count),
          })) ?? []
        }
        selected={query.view ?? ""}
        label={p.text("input.reminderView.label")}
        loading={views.reminders.loading}
        onSelect={(view) => {
          if (isTerm("reminderView", view)) {
            change({ ...query, view });
          }
        }}
        controls={
          <div className="work-queue-filters">
            <Select
              label={p.text("ui.reminderState")}
              value={query.state ?? ""}
              options={[
                { value: "", label: p.text("ui.anyFilter") },
                ...(["PENDING", "COMPLETED", "CANCELLED"] as const).map((value) => ({
                  value,
                  label: p.term("reminderState", value),
                })),
              ]}
              onChange={(value) => {
                const { state: _state, ...rest } = query;
                if (value === "") {
                  change(rest);
                } else if (isTerm("reminderState", value)) {
                  change({ ...rest, state: value });
                }
              }}
            />
            <Select
              label={p.text("ui.reminderDue")}
              value={query.due ?? ""}
              options={[
                { value: "", label: p.text("ui.anyFilter") },
                ...(["OVERDUE", "TODAY", "UPCOMING"] as const).map((value) => ({
                  value,
                  label: p.term("reminderDue", value),
                })),
              ]}
              onChange={(value) => {
                const { due: _due, ...rest } = query;
                if (value === "") {
                  change(rest);
                } else if (isTerm("reminderDue", value)) {
                  change({ ...rest, due: value });
                }
              }}
            />
            <label className="field-toggle">
              <input
                type="checkbox"
                checked={query.includeInactive === true}
                onChange={(event) => {
                  const { includeInactive: _inactive, ...rest } = query;
                  change(event.target.checked ? { ...rest, includeInactive: true } : rest);
                }}
              />
              {p.text("ui.includeInactiveWork")}
            </label>
            <ReminderSearch
              query={query}
              change={change}
              draft={draft.value}
              onDraft={(value) => {
                setDraft({ source, value });
              }}
            />
          </div>
        }
      >
        <ReminderQueueWork
          filter={query}
          onLockChange={onLockChange}
          disclosureAvailable={disclosureAvailable}
        />
      </ViewTabs>
    </section>
  );
};
export const ReminderQueue = (props: QueueProps) => {
  const route = useContentRoute();
  const query = useMemo(
    () => parseReminderQuery(route.screen === "reminders" ? route.query : ""),
    [route],
  );
  const views = useWorkspaceViews().reminders;
  if (query.view === undefined) {
    const first = views.data?.items[0];
    if (first !== undefined) {
      return <DefaultReminderView query={query} view={first.view} />;
    }
    return <WorkspaceViewsStatus area="reminders" />;
  }
  return <ReadyReminderQueue query={query} {...props} />;
};
