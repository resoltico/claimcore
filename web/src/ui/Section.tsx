import type { ReactNode } from "react";
import "./section.css";

export const Section = ({
  id,
  title,
  actions,
  empty,
  children,
}: {
  id: string;
  title: ReactNode;
  actions?: ReactNode;
  empty?: ReactNode;
  children?: ReactNode;
}) => (
  <section className="section-card" aria-labelledby={id}>
    <header className="section-card-heading">
      <h2 id={id}>{title}</h2>
      {actions}
    </header>
    <div className="section-card-body">{children ?? empty}</div>
  </section>
);
