# Vendored from magpie

`host.js` and `pi.js` are magpie's plugin host (https://github.com/yetone/magpie,
`internal/plugin/`), MIT-licensed — see `LICENSE-magpie.txt`. They are kept **unmodified**:
Astra speaks magpie's host protocol so OpenCode and pi plugins written for magpie run as they are.
The server embeds them and writes them out by content hash (`HostAssets`); a test checks the hashes
below, so a change here is always a deliberate re-vendor.

Refresh with `node scripts/vendor-magpie-host.mjs [<commit>]` (or `--from <magpie clone>`, offline), which rewrites this file. Read the
diff before committing: everything in host.js runs with the user's privileges.

| File | magpie commit | SHA-256 |
| --- | --- | --- |
| host.js | cb4ad12bd1038c93cae08c877aac73bd1c4c1c2e | 77be460f4d089dd67f9744f9e0db7cbc204fc8a98f9cb23645391aa45d6e7cb2 |
| pi.js | cb4ad12bd1038c93cae08c877aac73bd1c4c1c2e | f1950b4832339c86282d7267d919dd5625c38f9d31ac5facad3f495b6f2053c8 |
