import { test } from "node:test";
import assert from "node:assert/strict";
import { engineFor } from "./helpers.ts";

const DOC = `<!doctype html><html><body>
  <table id="t"><tr class="row"><td>a</td><td data-x="1">  b
     c </td></tr><tr class="row"><td>d</td></tr></table>
  <div class="g"><a href="/1">One</a><span>own <b>bold</b> tail</span></div>
  <div class="g empty"></div>
</body></html>`;

async function sel(code: string) {
  const { engine } = await engineFor(`const DOC = ${JSON.stringify(DOC)};\nexport function f() { ${code} }`);
  return engine.call("f", []);
}

test("selects on documents and within elements (descendants only)", async () => {
  assert.deepEqual(await sel(`return html.select(DOC, "tr.row").map((r) => html.select(r, "td").length);`), {
    ok: true,
    value: [2, 1],
  });
  assert.deepEqual(await sel(`const t = html.select(DOC, "table")[0]; return html.select(t, "table").length;`), {
    ok: true,
    value: 0,
  });
});

test("text normalisation, ownText, attributes, tag", async () => {
  assert.deepEqual(
    await sel(`const td = html.select(DOC, "td[data-x]")[0];
               const span = html.select(DOC, "div.g span")[0];
               return [td.text(), td.attr("data-x"), td.attr("missing"), td.tag, span.ownText(), span.text()];`),
    { ok: true, value: ["b c", "1", null, "td", "own tail", "own bold tail"] },
  );
});

test(":has(), attribute and structural selectors are supported", async () => {
  assert.deepEqual(
    await sel(`return [
      html.select(DOC, "div.g:has(a)").length,
      html.select(DOC, "a[href^='/']").length,
      html.select(DOC, "tr:first-child td:last-child").length,
      html.select(DOC, "div.g:not(.empty)").length,
    ];`),
    { ok: true, value: [1, 1, 1, 1] },
  );
});

test("non-standard pseudo-classes are rejected", async () => {
  for (const s of [":contains(One)", "td:eq(0)", "tr:first", "td:last", "tr:odd", ":header", "div:parent"]) {
    const res = await sel(`return html.select(DOC, ${JSON.stringify(s)});`);
    assert.equal(!res.ok && res.error.type === "SourceError" && res.error.kind, "InvalidInput", s);
  }
});

test("elements cannot be forged", async () => {
  const res = await sel(`return html.select({ tag: "div" }, "a");`);
  assert.equal(!res.ok && res.error.type === "SourceError" && res.error.kind, "InvalidInput");
});
