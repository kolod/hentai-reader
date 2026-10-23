// Contract test: html.select on strings and elements, the portable selector subset, text normalisation.
// Runs inside the host's own engine; run() throws on failure.

function assert(cond, message) {
  if (!cond) throw new Error("assertion failed: " + message);
}

async function expectError(kind, fn) {
  try {
    await fn();
  } catch (e) {
    if (e instanceof SourceError && e.kind === kind) return;
    throw new Error("expected SourceError(" + kind + "), got " + (e && e.name) + ": " + (e && e.message));
  }
  throw new Error("expected SourceError(" + kind + "), got success");
}

export const manifest = {
  "id": "html-select",
  "name": "html-select",
  "version": "1.0.0",
  "contract": "1.0",
  "domains": [
    "a.test"
  ],
  "politeness": {
    "minIntervalMs": 0,
    "maxConcurrency": 4
  },
  "login": "none",
  "filters": [],
  "capabilities": {
    "suggestTags": false
  }
};

const DOC = "<!doctype html><html><head><title>T</title></head><body>" +
  "<div id='list' class='box'>" +
  "<a class='item first' href='/g/1' data-n='1'>One\n   <b>bold</b></a>" +
  "<a class='item' href='/g/2' data-n='2'>Two&nbsp;x</a>" +
  "<a class='item last' href='https://x.test/3' data-n='3'> Three </a>" +
  "<span class='empty'></span>" +
  "</div>" +
  "<table><tr><td>r1c1</td><td>r1c2</td></tr><tr><td>r2c1</td></tr></table>" +
  "<p>own <i>inner</i> text</p>" +
  "</body></html>";

function count(target, selector) {
  return html.select(target, selector).length;
}

export async function run() {
  const items = html.select(DOC, "#list a.item");
  assert(items.length === 3, "class and id selectors");
  assert(items.map((e) => e.attr("data-n")).join(",") === "1,2,3", "document order");
  assert(items[0].tag === "a", "lower-case tag");
  assert(items[0].text() === "One bold", "whitespace collapsed and trimmed: " + items[0].text());
  assert(items[1].text() === "Two\u00a0x", "non-breaking space kept");
  assert(items[2].text() === "Three", "trimmed");
  assert(items[0].attr("href") === "/g/1", "raw attribute, URL not resolved");
  assert(items[0].attr("missing") === null, "missing attribute is null");
  assert(html.select(DOC, "p")[0].ownText() === "own text", "ownText excludes child elements");
  assert(html.select(items[0], "b")[0].html() === "bold", "inner html");

  const box = html.select(DOC, "div.box")[0];
  assert(count(box, "a") === 3, "select within an element");
  assert(count(box, "div") === 0, "an element does not match itself");
  const rows = html.select(DOC, "tr");
  assert(rows.length === 2 && count(rows[0], "td") === 2 && count(rows[1], "td") === 1, "nested selection in tables");

  assert(count(DOC, "a[href^='/g/']") === 2, "attribute prefix");
  assert(count(DOC, "a[href$='/3']") === 1, "attribute suffix");
  assert(count(DOC, "a[href*='x.test']") === 1, "attribute substring");
  assert(count(DOC, "a[data-n='2']") === 1, "attribute equals");
  assert(count(DOC, "a[data-n]") === 3, "attribute presence");
  assert(count(DOC, "div > a") === 3, "child combinator");
  assert(count(DOC, "a.first + a") === 1, "adjacent sibling");
  assert(count(DOC, "a.first ~ a") === 2, "general sibling");
  assert(count(DOC, "a:first-child") === 1, ":first-child");
  assert(count(DOC, "td:last-child") === 2, ":last-child");
  assert(count(DOC, "#list > :nth-child(2)") === 1, ":nth-child");
  assert(count(DOC, "a:not(.first)") === 2, ":not");
  assert(count(DOC, "span:empty") === 1, ":empty");
  assert(count(DOC, "div:has(span.empty)") === 1, ":has");
  assert(count(DOC, "a, td") === 6, "selector list");

  for (const bad of ["a:contains(One)", "a:eq(0)", "a:first", "a:last", "tr:even", "tr:odd", ":header", "div:parent"]) {
    await expectError("InvalidInput", async () => html.select(DOC, bad));
  }
  await expectError("InvalidInput", async () => html.select(DOC, "div["));
  await expectError("InvalidInput", async () => html.select({ tag: "div" }, "a"));
}
