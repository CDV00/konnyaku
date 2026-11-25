import { expect } from "@playwright/test";
import { mailpitUrl } from "./env";

/**
 * Reading email in a test, without picking up someone else's message.
 *
 * Tests run in parallel against one Mailpit, and the Worker also sends mail the test did not ask for: a fresh
 * Consultant can be assigned a lead from `public-lead.spec.ts` and get "Lead mới" at the same moment it waits for its
 * reset link. So "the newest email to this address" is a race. Instead:
 *
 *   const inbox = await mailbox(staff.email);         // 1. before the action: remember what is already there
 *   await send.click();                                // 2. the action that sends the email
 *   const email = await inbox.next({ link: "/login/reset" }); // 3. wait for a NEW email that is the one we expect
 *
 * The recipient should also be unique to the test (`createStaff`/`createLearner` add `uniqueTag()`).
 */
export type Email = { id: string; subject: string; text: string; html: string };
export type EmailMatch = { subject?: string | RegExp; link?: string };

type Summary = { ID: string; Subject: string };

async function search(address: string): Promise<Summary[]> {
  const query = encodeURIComponent(`to:"${address}"`);
  const response = await fetch(`${mailpitUrl}/api/v1/search?query=${query}&limit=200`);
  if (!response.ok) throw new Error(`Mailpit search ${response.status}: ${await response.text()}`);
  return ((await response.json()) as { messages: Summary[] }).messages;
}

async function read(id: string): Promise<Email> {
  const message = (await (await fetch(`${mailpitUrl}/api/v1/message/${id}`)).json()) as { Subject: string; Text: string; HTML: string };
  return { id, subject: message.Subject, text: message.Text, html: message.HTML };
}

function links(email: { text: string; html: string }): string[] {
  return [...`${email.text}\n${email.html}`.matchAll(/https?:\/\/[^\s"'<>)]+/g)].map((m) => m[0].replaceAll("&amp;", "&"));
}

function matches(email: Email, match: EmailMatch): boolean {
  if (match.subject !== undefined) {
    const ok = typeof match.subject === "string" ? email.subject.includes(match.subject) : match.subject.test(email.subject);
    if (!ok) return false;
  }
  return match.link === undefined || links(email).some((l) => l.includes(match.link!));
}

/** The mailbox of one address, as it is now; `next()` returns only emails that arrive after this call. */
export async function mailbox(address: string) {
  const seen = new Set((await search(address)).map((m) => m.ID));
  const cache = new Map<string, Email>();
  return {
    /**
     * Waits (the Worker sends asynchronously) for an email to `address` that was not there before and fits `match`;
     * other new emails to the same address are skipped. Each call returns a different email.
     */
    async next(match: EmailMatch): Promise<Email> {
      let found: Email | undefined;
      const others: string[] = [];
      await expect
        .poll(
          async () => {
            for (const summary of (await search(address)).reverse()) {
              if (seen.has(summary.ID)) continue;
              const email = cache.get(summary.ID) ?? (await read(summary.ID));
              cache.set(summary.ID, email);
              if (matches(email, match)) {
                found = email;
                seen.add(email.id);
                return email.id;
              }
              if (!others.includes(email.subject)) others.push(email.subject);
            }
            return null;
          },
          {
            message: `a new email to ${address} matching ${JSON.stringify({ ...match, subject: match.subject?.toString() })} in Mailpit (other new emails: ${JSON.stringify(others)})`,
            timeout: 60_000,
          },
        )
        .not.toBeNull();
      return found!;
    },
  };
}

/** First link of an email whose URL contains `fragment`. */
export function linkIn(email: { text: string; html: string }, fragment: string): string {
  const all = links(email);
  const link = all.find((l) => l.includes(fragment));
  if (!link) throw new Error(`no link containing ${fragment} in the email; links: ${all.join(", ")}`);
  return link;
}
