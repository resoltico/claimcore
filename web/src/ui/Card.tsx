import "./card.css";
import { useId, type ReactNode } from "react";

export const Card = ({
  title,
  className = "",
  children,
}: {
  title: string;
  className?: string;
  children: ReactNode;
}) => {
  const titleId = useId();
  return (
    <section className={["ui-card", className].filter(Boolean).join(" ")} aria-labelledby={titleId}>
      <h2 id={titleId} className="ui-card-title">
        {title}
      </h2>
      {children}
    </section>
  );
};
