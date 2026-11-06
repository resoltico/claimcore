import { loadPreferences } from "../presentation/preferences";
// This module is evaluated before the app mounts and before the theme is painted.
const preferences = loadPreferences();
document.documentElement.dataset["mode"] = preferences.mode;
document.documentElement.dataset["accent"] = preferences.accent;
