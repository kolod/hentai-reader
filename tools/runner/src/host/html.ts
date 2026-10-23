// html.select for the reference host, backed by cheerio (parse5). Restricts selectors to the
// contract's portable subset so Jsoup, AngleSharp and cheerio agree (research R3).
import * as cheerio from "cheerio";
import type { AnyNode, Element } from "domhandler";
import { envelopeError, envelopeValue } from "./envelope.ts";

/** Non-standard pseudo-classes offered by css-select/jQuery but not by every host parser. */
const FORBIDDEN_PSEUDO =
  /:(contains|icontains|eq|gt|lt|first|last|even|odd|header|parent|selected|checkbox|radio|password|file|submit|reset|button|input|text|image|animated|hidden|visible)(?![\w-])/i;

const DOC_CACHE_SIZE = 8;

export function collapseWhitespace(text: string): string {
  return text.replace(/[ \t\n\r\f]+/g, " ").trim();
}

export class HtmlHost {
  private docs = new Map<string, cheerio.CheerioAPI>();
  private handles = new Map<number, { $: cheerio.CheerioAPI; node: Element }>();
  private nextId = 1;

  select(markup: string, elementId: number, selector: string): string {
    const forbidden = FORBIDDEN_PSEUDO.exec(selector);
    if (forbidden) {
      return envelopeError("InvalidInput", `html.select: non-standard selector ${forbidden[0]} is not supported`);
    }
    try {
      let $: cheerio.CheerioAPI;
      let nodes: AnyNode[];
      if (elementId < 0) {
        $ = this.document(markup);
        nodes = $(selector).toArray();
      } else {
        const handle = this.handles.get(elementId);
        if (!handle) return envelopeError("InvalidInput", "html.select: element is no longer valid");
        $ = handle.$;
        nodes = $(handle.node).find(selector).toArray();
      }
      const out = [];
      for (const node of nodes) {
        if (node.type !== "tag" && node.type !== "script" && node.type !== "style") continue;
        const id = this.nextId++;
        this.handles.set(id, { $, node: node as Element });
        out.push({ id, tag: (node as Element).name.toLowerCase() });
      }
      return envelopeValue(out);
    } catch (e) {
      return envelopeError("InvalidInput", `html.select: invalid selector '${selector}': ${(e as Error).message}`);
    }
  }

  call(op: string, elementId: number, arg: string): string {
    const handle = this.handles.get(elementId);
    if (!handle) return envelopeError("InvalidInput", "html: element is no longer valid");
    const { $, node } = handle;
    switch (op) {
      case "text":
        return envelopeValue(collapseWhitespace($(node).text()));
      case "ownText":
        return envelopeValue(
          collapseWhitespace(
            node.children
              .filter((c) => c.type === "text")
              .map((c) => (c as unknown as { data: string }).data)
              .join(""),
          ),
        );
      case "html":
        return envelopeValue($(node).html() ?? "");
      case "outerHtml":
        return envelopeValue($.html(node));
      case "attr":
        return envelopeValue($(node).attr(arg) ?? null);
      default:
        return envelopeError("InvalidInput", `html: unknown operation ${op}`);
    }
  }

  /** Drops element handles; called when no source call is in flight. */
  releaseHandles(): void {
    this.handles.clear();
  }

  private document(markup: string): cheerio.CheerioAPI {
    let $ = this.docs.get(markup);
    if ($) {
      this.docs.delete(markup);
    } else {
      $ = cheerio.load(markup);
      if (this.docs.size >= DOC_CACHE_SIZE) this.docs.delete(this.docs.keys().next().value as string);
    }
    this.docs.set(markup, $);
    return $;
  }
}
