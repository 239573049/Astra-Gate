// Installs the in-memory host before the astra module reads it (imported first by astra.mjs).
import { createHost } from './host.mjs';

if (!globalThis.__astraV2) globalThis.__astraTestHost = createHost();
