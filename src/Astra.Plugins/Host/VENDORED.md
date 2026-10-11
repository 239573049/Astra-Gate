# Vendored from magpie

`host.js` and `pi.js` are magpie's plugin host (https://github.com/yetone/magpie,
`internal/plugin/`), MIT-licensed — see `LICENSE-magpie.txt`. They are kept **unmodified**:
Astra speaks magpie's host protocol so OpenCode and pi plugins written for magpie run as they are.
The server embeds them and writes them out by content hash (`HostAssets`); a test checks the hashes
below, so a change here is always a deliberate re-vendor.

Refresh with `node scripts/vendor-magpie-host.mjs [<commit>]`, which rewrites this file. Read the
diff before committing: everything in host.js runs with the user's privileges.

| File | magpie commit | SHA-256 |
| --- | --- | --- |
| host.js | ee20a1511bd3886106f2ab91a511e84a444964d3 | 6e85b6c721c84bc0326ec38e9dad67a105919e3b5712646750f502f5e03e7fcd |
| pi.js | ee20a1511bd3886106f2ab91a511e84a444964d3 | 84b7ce0cb7cb51bf63ffd88dca8dafcb41728eb2e7f0d4696bdc0037013161ee |
