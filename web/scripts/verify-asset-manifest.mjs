import { verifyManifest } from "./asset-manifest.mjs";

await verifyManifest();
process.stdout.write("ClaimCore Web asset manifest verified.\n");
