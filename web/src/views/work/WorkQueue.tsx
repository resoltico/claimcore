import { useMemo, useState } from "react";
import type { ReminderDue, ReminderState } from "@contracts/client";
import { usePresentation } from "../../presentation/context";
import { navigate } from "../../routing/navigation";
import { ReminderList } from "./ReminderList";
import { WorkOverview } from "./WorkOverview";

export const WorkQueue = () => {
  const p = usePresentation();
  const [mine, setMine] = useState(false);
  const [due, setDue] = useState<ReminderDue | "">("");
  const [state, setState] = useState<ReminderState>("PENDING");
  const [includeInactive, setIncludeInactive] = useState(false);
  const [text, setText] = useState("");
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
          <button type="button" aria-pressed={!mine} onClick={() => setMine(false)}>
            {p.text("ui.accessibleWork")}
          </button>
          <button type="button" aria-pressed={mine} onClick={() => setMine(true)}>
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
          <button type="button" onClick={() => navigate("/cases?due=UNSCHEDULED")}>
            {p.text("ui.casesWithoutNextAction")}
          </button>
          <button type="button" onClick={() => navigate("/cases?assignment=UNASSIGNED")}>
            {p.text("ui.unassignedCases")}
          </button>
        </div>
        <label>
          {p.text("ui.reminderState")}
          <select value={state} onChange={(event) => setState(event.target.value as ReminderState)}>
            {(["PENDING", "COMPLETED", "CANCELLED"] as const).map((value) => (
              <option key={value} value={value}>
                {p.term("reminderState", value)}
              </option>
            ))}
          </select>
        </label>
        <label>
          {p.text("ui.reminderDue")}
          <select value={due} onChange={(event) => setDue(event.target.value as ReminderDue | "")}>
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
            onChange={(event) => setIncludeInactive(event.target.checked)}
          />
          {p.text("ui.includeInactiveWork")}
        </label>
        <label>
          {p.text("ui.search")}
          <input type="search" value={text} onChange={(event) => setText(event.target.value)} />
        </label>
      </div>
      {mine ? <WorkOverview mine /> : null}
      <ReminderList filter={filter} />
    </section>
  );
};
