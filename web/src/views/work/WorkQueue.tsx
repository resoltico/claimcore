import { useMemo, useState } from "react";
import type { ReminderListRequest, Reminder } from "@contracts/client";
import { usePresentation } from "../../presentation/context";
import { navigate } from "../../routing/navigation";
import { ReminderList } from "./ReminderList";
import { WorkOverview } from "./WorkOverview";

type ReminderDue = NonNullable<ReminderListRequest["due"]>;
type ReminderState = Reminder["state"];
export const WorkQueue = () => {
  const p = usePresentation();
  const [mine, setMine] = useState(false);
  const [due, setDue] = useState<ReminderDue | "">("");
  const [state, setState] = useState<ReminderState>("PENDING");
  const [includeInactive, setIncludeInactive] = useState(false);
  const [text, setText] = useState("");
  const [draft, setDraft] = useState("");
  const filter = useMemo(
    () => ({
      state,
      includeInactive,
      ...(mine ? { assignment: "MINE" as const } : {}),
      ...(due === "" ? {} : { due }),
      ...(text === "" ? {} : { text }),
    }),
    [state, includeInactive, mine, due, text],
  );
  return (
    <section className="work-queue" aria-labelledby="work-queue-title">
      <h1 id="work-queue-title" tabIndex={-1}>
        {p.text("ui.reminders")}
      </h1>
      <div className="work-queue-filters">
        <div className="actions">
          <button
            type="button"
            aria-pressed={!mine}
            onClick={() => {
              setMine(false);
            }}
          >
            {p.text("ui.accessibleWork")}
          </button>
          <button
            type="button"
            aria-pressed={mine}
            onClick={() => {
              setMine(true);
            }}
          >
            {p.text("ui.myWork")}
          </button>
          <button
            type="button"
            onClick={() => {
              setDue("OVERDUE");
              setState("PENDING");
            }}
          >
            {p.text("ui.overdueReminders")}
          </button>
          <button
            type="button"
            onClick={() => {
              setDue("TODAY");
              setState("PENDING");
            }}
          >
            {p.term("reminderDue", "TODAY")}
          </button>
          <button
            type="button"
            onClick={() => {
              navigate("/cases?due=UNSCHEDULED");
            }}
          >
            {p.text("ui.casesWithoutNextAction")}
          </button>
          <button
            type="button"
            onClick={() => {
              navigate("/cases?assignment=UNASSIGNED");
            }}
          >
            {p.text("ui.unassignedCases")}
          </button>
        </div>
        <label>
          {p.text("ui.reminderState")}
          <select
            value={state}
            onChange={(event) => {
              const { value } = event.target;
              if (value === "PENDING" || value === "COMPLETED" || value === "CANCELLED") {
                setState(value);
              }
            }}
          >
            {(["PENDING", "COMPLETED", "CANCELLED"] as const).map((value) => (
              <option key={value} value={value}>
                {p.term("reminderState", value)}
              </option>
            ))}
          </select>
        </label>
        <label>
          {p.text("ui.reminderDue")}
          <select
            value={due}
            onChange={(event) => {
              const { value } = event.target;
              if (
                value === "" ||
                value === "OVERDUE" ||
                value === "TODAY" ||
                value === "UPCOMING"
              ) {
                setDue(value);
              }
            }}
          >
            <option value="">{p.text("ui.anyFilter")}</option>
            {(["OVERDUE", "TODAY", "UPCOMING"] as const).map((value) => (
              <option key={value} value={value}>
                {p.term("reminderDue", value)}
              </option>
            ))}
          </select>
        </label>
        <label>
          <input
            type="checkbox"
            checked={includeInactive}
            onChange={(event) => {
              setIncludeInactive(event.target.checked);
            }}
          />
          {p.text("ui.includeInactiveWork")}
        </label>
        <form
          role="search"
          onSubmit={(event) => {
            event.preventDefault();
            setText(draft);
          }}
        >
          <label>
            {p.text("ui.search")}
            <input
              type="search"
              value={draft}
              onChange={(event) => {
                setDraft(event.target.value);
              }}
            />
          </label>
          <button type="submit">{p.text("ui.search")}</button>
        </form>
      </div>
      {mine ? <WorkOverview mine /> : null}
      <ReminderList filter={filter} />
    </section>
  );
};
