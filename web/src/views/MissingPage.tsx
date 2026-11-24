import { useDocumentTitle } from "../hooks/useDocumentTitle";
import { PageTitle } from "../ui/Page";
import "./missing-page.css";
import { useEffect, useRef } from "react";
import { usePresentation } from "../presentation/context";
import { navigate } from "../routing/navigation";
import { followLink } from "../routing/followLink";

export const MissingPage = ({ inWorkspace = false }: { inWorkspace?: boolean }) => {
  const Surface = inWorkspace ? "section" : "main";
  const p = usePresentation();
  useDocumentTitle(p.text("ui.pageNotFound"));
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    heading.current?.focus();
  }, []);
  return (
    <Surface className="missing-page">
      <PageTitle id="missing-page-title" title={p.text("ui.pageNotFound")} headingRef={heading} />
      <p>{p.text("ui.pageNotFoundDescription")}</p>
      <a
        href="/cases"
        onClick={(event) => {
          followLink(event, () => {
            navigate("/cases");
          });
        }}
      >
        {p.text("label.cases")}
      </a>
    </Surface>
  );
};
