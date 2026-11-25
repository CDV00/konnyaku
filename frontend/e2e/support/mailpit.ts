import { expect } from "@playwright/test";
import { mailpitUrl } from "./env";

type Summary = { ID: string; Subject: string; To: { Address: string }[] };

/** The newest email Mailpit received for an address; polls because the Worker sends it asynchronously. */
export async function latestEmailTo(address: string): Promise<{ subject: string; text: string; html: string }> {
  let found: Summary | undefined;
  await expect
    .poll(
      async () => {
        const response = await fetch(`${mailpitUrl}/api/v1/search?query=${encodeURIComponent(`to:"${address}"`)}&limit=1`);
        const body = (await response.json()) as { messages: Summary[] };
        found = body.messages[0];
        return found?.ID ?? null;
      },
      { message: `an email to ${address} in Mailpit`, timeout: 60_000 },
    )
    .not.toBeNull();
  const message = (await (await fetch(`${mailpitUrl}/api/v1/message/${found!.ID}`)).json()) as { Subject: string; Text: string; HTML: string };
  return { subject: message.Subject, text: message.Text, html: message.HTML };
}

/** First link of an email whose URL contains `fragment`. */
export function linkIn(email: { text: string; html: string }, fragment: string): string {
  const links = [...`${email.text}\n${email.html}`.matchAll(/https?:\/\/[^\s"'<>)]+/g)].map((m) => m[0].replaceAll("&amp;", "&"));
  const link = links.find((l) => l.includes(fragment));
  if (!link) throw new Error(`no link containing ${fragment} in the email; links: ${links.join(", ")}`);
  return link;
}
