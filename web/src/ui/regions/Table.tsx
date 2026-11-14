import type { RowLink, ActionPresentation, LinkNavigation } from "./interaction";
import type { RegionRow, Affordance, RegionContent } from "@contracts/client";
/* oxlint-disable jsx-a11y/no-noninteractive-tabindex -- The sole named scroll region must support keyboard scrolling, including empty tables. */
import { Actions } from "./Actions";
import { coreRowLink } from "./rowLink";
import { MoneyMeter } from "./MoneyMeter";
import { SectionsView } from "../SectionsView";
import { RowDetails } from "./RowDetails";
import { followLink } from "../../routing/followLink";
import {} from "./table.css";
import { useId } from "react";

import { PresentedValue } from "../../components/PresentedValue";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";

type TableContent = Extract<RegionContent, { kind: "TABLE" }>;
const rowNavigation = (
  row: RegionRow,
  columns: TableContent["columns"],
  navigation: LinkNavigation | undefined,
): RowLink | undefined => {
  const link = coreRowLink(row.link, navigation);
  const kind = row.link?.endpoint === "people.grants" ? "ACTOR" : "REFERENCE";
  const matching = row.values.findIndex((value) => value.kind === kind);
  const primary = columns.findIndex((column) => column.prominence === "PRIMARY");
  const linkColumn = columns[matching < 0 ? Math.max(primary, 0) : matching]?.key;
  return link === undefined
    ? undefined
    : {
        ...link,
        ...(linkColumn === undefined ? {} : { column: linkColumn }),
      };
};
export const Table = ({
  view,
  timeZone = "UTC",
  actionsDisabled,
  linkNavigation,
  actionPresentation,
  onAction,
  labelledBy,
  sectionLink,
}: {
  view: Extract<RegionContent, { kind: "TABLE" }>;
  timeZone?: string;
  actionsDisabled?: boolean;
  linkNavigation?: LinkNavigation;
  actionPresentation?: ActionPresentation;
  onAction?: (action: Affordance, row?: RegionRow, originId?: string) => void;
  labelledBy?: string;
  sectionLink?: RowLink;
}) => {
  const p = usePresentation();
  const tableId = useId();
  const sections = view.sections ?? [];
  const renderRow = (row: Extract<RegionContent, { kind: "TABLE" }>["rows"][number]) => {
    const link = rowNavigation(row, view.columns, linkNavigation);
    return (
      <tr key={row.key}>
        {row.values.map((value, position) => (
          <td
            key={view.columns[position]?.key}
            id={`${tableId}-${row.key}-${view.columns[position]?.key}`}
            data-column={view.columns[position]?.key}
            data-prominence={view.columns[position]?.prominence}
            data-money={view.columns[position]?.isMoney}
          >
            {link !== undefined &&
            (link.column === undefined
              ? value.kind === (link.kind ?? "REFERENCE")
              : view.columns[position]?.key === link.column) ? (
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
        {view.rows.some((item) => item.detail !== undefined) ? (
          <td className="table-row-detail">
            {row.detail === undefined ? null : <RowDetails value={row.detail} zone={timeZone} />}
          </td>
        ) : null}
        {view.rows.some((item) => (item.actions ?? []).length > 0) ? (
          <td>
            <div
              className="row-actions"
              role="group"
              aria-labelledby={view.columns
                .filter((column) => column.prominence === "PRIMARY")
                .map((column) => `${tableId}-${row.key}-${column.key}`)
                .join(" ")}
            >
              <Actions
                disabled={actionsDisabled ?? false}
                actions={row.actions ?? []}
                {...(actionPresentation === undefined ? {} : { actionPresentation })}
                {...(onAction === undefined
                  ? {}
                  : {
                      onAction: (action, originId) => {
                        onAction(action, row, originId);
                      },
                    })}
                compact
              />
            </div>
          </td>
        ) : null}
      </tr>
    );
  };
  return (
    <>
      {view.meter === undefined ? null : (
        <MoneyMeter meter={view.meter} timeZone={timeZone} label={p.text("label.paid")} />
      )}
      <div
        className="table-view"
        role="group"
        tabIndex={0}
        {...(labelledBy === undefined
          ? { "aria-label": p.text("label.results") }
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
              {view.rows.some((item) => item.detail !== undefined) ? (
                <th scope="col">{p.text("label.recordDetails")}</th>
              ) : null}
              {view.rows.some((item) => (item.actions ?? []).length > 0) ? (
                <th scope="col">{p.text("label.availableActs")}</th>
              ) : null}
            </tr>
          </thead>
          {(view.groups ?? []).length === 0 ? (
            <tbody>{view.rows.map(renderRow)}</tbody>
          ) : (
            view.groups?.map((group) => (
              <tbody key={group.titleId}>
                <tr className="table-group">
                  <th
                    scope="rowgroup"
                    colSpan={
                      view.columns.length +
                      (view.rows.some((item) => (item.actions ?? []).length > 0) ? 1 : 0) +
                      (view.rows.some((item) => item.detail !== undefined) ? 1 : 0)
                    }
                  >
                    {renderCatalogKey(p, group.titleId)} ·{" "}
                    <PresentedValue
                      value={{ kind: "COUNT", value: group.count }}
                      timeZone={timeZone}
                    />
                  </th>
                </tr>
                {group.rowKeys.map((key) => {
                  const index = view.rows.findIndex((row) => row.key === key);
                  const row = view.rows[index];
                  return row === undefined ? null : renderRow(row);
                })}
              </tbody>
            ))
          )}
        </table>
      </div>
      {sections.length === 0 ? null : (
        <SectionsView
          sections={sections}
          headingLevel={2}
          timeZone={timeZone}
          {...(sectionLink === undefined ? {} : { link: sectionLink })}
        />
      )}
    </>
  );
};

/* oxlint-enable jsx-a11y/no-noninteractive-tabindex */
