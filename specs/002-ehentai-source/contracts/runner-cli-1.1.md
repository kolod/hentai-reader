# Runner changes for contract 1.1 — delta to 001 `contracts/runner-cli.md`

## `run`

```text
source-runner run sources/ehentai search '{"text":"","filters":{}}' null --setting site=exhentai --live --cookies cookies-eh.txt
source-runner run sources/ehentai getAccountStatus --live --cookies cookies-eh.txt
```

- `--setting <id>=<value>` (repeatable): validated against the manifest's `settings`; unknown id or
  value → exit 2. Values are merged over manifest defaults.
- For 1.1 sources the runner appends the settings object to the call (and `null` for an omitted
  `retry`); for 1.0 sources it calls with the 1.0 arity (contract-1.1.md › Calling convention).
- `getAccountStatus` is accepted only when `manifest.capabilities.accountStatus` is true.

## Test cases

```json
{
  "description": "US5: ExHentai front page",
  "settings": { "site": "exhentai" },
  "call": { "fn": "search", "args": [{ "text": "", "filters": {} }, null] }
}
```

- `settings` (optional) merged over manifest defaults; `args` never contain the settings object.
- `fn` may be `getAccountStatus` (result validated as `AccountStatus`).

## `check`

Additional rules: `capabilities.accountStatus` ⇔ `getAccountStatus` exported; each setting id
unique and `default ∈ values` (schema + runtime); fixture audit unchanged (redaction idempotence).

## `contract-test`

New `contract/tests/settings-passing/`: `expect.json` gains `"settings": {…}`; the harness passes
them to `run(settings)`; the test asserts it received exactly the declared, merged values.

## Recording with an account

`--cookies <file>` (Netscape format; file name must match `cookies*.txt`, gitignored). Never
committed; the recorder already drops cookie headers and the redactor drops personal page parts.
