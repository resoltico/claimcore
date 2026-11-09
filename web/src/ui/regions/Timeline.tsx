import type { RegionContent } from "@contracts/client";
import { usePresentation } from "../../presentation/context";
import { PresentedValue } from "../../components/PresentedValue";
import { SectionsView } from "../SectionsView";
import type { RowLink } from "./Table";
import "./timeline.css";
import { SuppliedMessage } from "../SuppliedMessage";
export const Timeline = ({
  content,
  timeZone,
  rowLink,
}: {
  content: Extract<RegionContent, { kind: "TIMELINE" }>;
  timeZone: string;
  rowLink?: (key: string) => RowLink | undefined;
}) => {
  const p = usePresentation();
  return (
    <ol className="region-timeline">
      {content.entries.map((entry) => (
        <li key={entry.key}>
          <h3>
            <SuppliedMessage message={entry.summary} timeZone={timeZone} />
          </h3>
          <SectionsView
            sections={entry.highlights}
            headingLevel={4}
            timeZone={timeZone}
            {...(rowLink?.(entry.key) === undefined ? {} : { link: rowLink(entry.key)! })}
          />
          <p>
            <PresentedValue value={{ kind: "ACTOR", value: entry.who }} timeZone={timeZone} /> ·{" "}
            <PresentedValue value={{ kind: "INSTANT", value: entry.when }} timeZone={timeZone} />
          </p>
          {entry.details.length === 0 ? null : (
            <details>
              <summary>{p.text("label.recordDetails")}</summary>
              <SectionsView
                sections={entry.details}
                headingLevel={4}
                timeZone={timeZone}
                {...(rowLink?.(entry.key) === undefined ? {} : { link: rowLink(entry.key)! })}
              />
            </details>
          )}
        </li>
      ))}
    </ol>
  );
};
