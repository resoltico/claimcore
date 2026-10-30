import "./missing-page.css";
import { useEffect, useRef } from "react";
import { usePresentation } from "../presentation/context";
import { navigate } from "../routing/navigation";
import { followLink } from "../routing/followLink";

export const MissingPage = () => {
  const p = usePresentation();
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    heading.current?.focus();
  }, []);
  return (
    <main className="missing-page">
      <h1 ref={heading} tabIndex={-1}>
        {p.text("ui.pageNotFound")}
      </h1>
      <p>{p.text("ui.pageNotFoundDescription")}</p>
      <a
        href="/cases"
        onClick={(event) => {
          followLink(event, () => {
            navigate("/cases");
          });
        }}
      >
        {p.text("ui.backToCases")}
      </a>
    </main>
  );
};
