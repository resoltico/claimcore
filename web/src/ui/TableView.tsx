/* oxlint-disable jsx-a11y/no-noninteractive-tabindex -- The sole named scroll region must support keyboard scrolling, including empty tables. */
import { followLink } from "../routing/followLink";
import "./presented-view.css";
import type { ReactNode } from "react";
import type { PresentedView } from "@contracts/client";
import { PresentedValue } from "../components/PresentedValue";
import { usePresentation } from "../presentation/context";
import { renderCatalogKey } from "../presentation/messages";

export type RowLink = {
  href: string;
  follow: () => void;
  kind?: "ACTOR" | "REFERENCE";
  label?: string;
  id?: string;
};

export const TableView = ({
  view,
  timeZone = "UTC",
  renderActions,
  rowLink,
  labelledBy,
}: {
  view: Extract<PresentedView, { kind: "TABLE" }>;
  timeZone?: string;
  renderActions?: (index: number) => ReactNode;
  rowLink?: (index: number) => RowLink | undefined;
  labelledBy?: string;
}) => {
  const p = usePresentation();
  return (
    <div
      className="table-view"
      role="group"
      tabIndex={0}
      {...(labelledBy === undefined
        ? { "aria-label": p.text("label.recordDetails") }
        : { "aria-labelledby": labelledBy })}
    >
      <table aria-labelledby={labelledBy}>
        <thead>
          <tr>
            {view.columns.map((column) => (
              <th key={column.key} scope="col" data-prominence={column.prominence}>
                {renderCatalogKey(p, column.labelId)}
              </th>
            ))}
            {renderActions === undefined ? null : (
              <th scope="col">{p.text("label.availableActs")}</th>
            )}
          </tr>
        </thead>
        <tbody>
          {view.rows.map((row, index) => {
            const link = rowLink?.(index);
            return (
              // oxlint-disable-next-line react/no-array-index-key -- Rows have positional identity in the supplied view and pages clear before replacement.
              <tr key={index}>
                {row.map((value, position) => (
                  <td
                    key={view.columns[position]?.key}
                    data-column={view.columns[position]?.key}
                    data-prominence={view.columns[position]?.prominence}
                    data-money={view.columns[position]?.isMoney}
                  >
                    {link !== undefined && value.kind === (link.kind ?? "REFERENCE") ? (
                      <a
                        href={link.href}
                        id={link.id}
                        aria-label={link.label}
                        className="row-link"
                        onClick={(event) => {
                          followLink(event, link.follow);
                        }}
                      >
                        <PresentedValue value={value} timeZone={timeZone} />
                      </a>
                    ) : (
                      <PresentedValue value={value} timeZone={timeZone} />
                    )}
                  </td>
                ))}
                {renderActions === undefined ? null : <td>{renderActions(index)}</td>}
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
};

/* oxlint-enable jsx-a11y/no-noninteractive-tabindex */
