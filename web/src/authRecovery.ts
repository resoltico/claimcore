import { languageDirection, loadPreferenceState } from "./presentation/preferences";
import { loadLanguage } from "./presentation/languageCatalog";
import { translate } from "./presentation/messages";
import { loginPath } from "./routing/loginPath";

import catalogAssets from "virtual:claimcore-catalogs";
import { installCatalogAssets } from "./api/catalog";

installCatalogAssets(catalogAssets);

const { preferences, persistence } = loadPreferenceState();
void loadLanguage(preferences.language)
  .then(() => {
    const heading = document.getElementById("sign-in-incomplete");
    const restart = document.getElementById("restart-sign-in");
    if (heading === null || !(restart instanceof HTMLAnchorElement)) {
      return;
    }
    document.documentElement.lang = preferences.language;
    document.documentElement.dir = languageDirection(preferences.language);
    heading.textContent = translate(preferences, "ui.signInIncomplete");
    if (persistence.kind === "incompatible") {
      const warning = document.createElement("p");
      warning.setAttribute("role", "status");
      warning.textContent = translate(preferences, "ui.incompatiblePreferences");
      heading.after(warning);
    }
    restart.textContent = translate(preferences, "ui.restartSignIn");
    restart.href = loginPath(preferences.language);
  })
  .catch(() => {
    // The static same-origin action remains available.
  });
