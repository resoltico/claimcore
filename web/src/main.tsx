import "./theme/boot";
import { PresentationProvider } from "./presentation/PresentationProvider";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { IconSprite } from "./ui/icons";
import { App } from "./App";
import "./theme/tokens.css";
import "./styles.css";
import "./login.css";
import "./ui/layout.css";

const root = document.getElementById("root");
if (root === null) {
  throw new Error("Missing ClaimCore root element.");
}

createRoot(root).render(
  <StrictMode>
    <PresentationProvider>
      <IconSprite />
      <App />
    </PresentationProvider>
  </StrictMode>,
);
