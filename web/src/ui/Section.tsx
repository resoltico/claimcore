import type { ReactNode, ComponentPropsWithoutRef } from "react";
import { sectionCountMessages, type RegionCount } from "@contracts/client";
import { usePresentation } from "../presentation/context";
import "./section.css";

export const Section = ({
  id,
  title,
  count,
  actions,
  empty,
  children,
  className,
  ...section
}: Omit<ComponentPropsWithoutRef<"section">, "title"> & {
  id: string;
  title: ReactNode;
  count?: RegionCount | undefined;
  actions?: ReactNode;
  empty?: ReactNode;
}) => {
  const p = usePresentation();
  const caption =
    count === undefined || count.value <= 0
      ? null
      : p.text(
          sectionCountMessages[count.kind][new Intl.PluralRules(p.language).select(count.value)],
          { count: p.integer(count.value) },
        );
  return (
    <section
      className={["section-card", className].filter(Boolean).join(" ")}
      aria-labelledby={id}
      {...section}
    >
      <div className="section-card-heading">
        <div>
          <h2 id={id} tabIndex={-1}>
            {title}
          </h2>
          {caption === null ? null : <span className="section-count">{caption}</span>}
        </div>
        {actions}
      </div>
      <div className="section-card-body">{children ?? empty}</div>
    </section>
  );
};
