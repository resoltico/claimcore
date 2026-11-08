/* oxlint-disable jsx-a11y/no-noninteractive-tabindex -- The sole named scroll region must support keyboard scrolling, including empty tables. */
import { RecordView } from "./RecordView";
import { SuppliedMessage } from "./SuppliedMessage";
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
    <>
      {view.sections.length === 0 ? null : (
        <RecordView view={{ kind: "RECORD", sections: view.sections }} timeZone={timeZone} />
      )}
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
              {view.details.some((detail) => detail !== null) ? (
                <th scope="col">{p.text("label.recordDetails")}</th>
              ) : null}
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
                  {view.details.some((detail) => detail !== null) ? (
                    <td className="table-row-detail">
                      {view.details[index] === null || view.details[index] === undefined ? null : (
                        <>
                          {view.details[index].summary === null ? null : (
                            <p className="row-summary">
                              <SuppliedMessage
                                message={view.details[index].summary}
                                timeZone={timeZone}
                              />
                            </p>
                          )}
                          <RecordView
                            view={{ kind: "RECORD", sections: view.details[index].highlights }}
                            timeZone={timeZone}
                          />
                          <details>
                            <summary>{p.text("label.recordDetails")}</summary>
                            <RecordView
                              view={{ kind: "RECORD", sections: view.details[index].sections }}
                              timeZone={timeZone}
                            />
                          </details>
                        </>
                      )}
                    </td>
                  ) : null}
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </>
  );
};

/* oxlint-enable jsx-a11y/no-noninteractive-tabindex */
