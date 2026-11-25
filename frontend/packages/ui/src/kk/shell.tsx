"use client";

import { useState, type ReactNode } from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import {
  BookOpen,
  CalendarDays,
  ClipboardList,
  GraduationCap,
  Building2,
  Home,
  Layers,
  LogOut,
  Menu,
  PhoneCall,
  Receipt,
  ShieldCheck,
  Users,

  Wallet,
  type LucideIcon,
} from "lucide-react";
import { cn } from "@konnyaku/ui/lib/utils";
import { Highlight } from "@konnyaku/ui/components/animate-ui/primitives/effects/highlight";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@konnyaku/ui/components/animate-ui/components/radix/dropdown-menu";
import { Sheet, SheetContent, SheetTitle, SheetTrigger } from "@konnyaku/ui/components/animate-ui/components/radix/sheet";
import { ThemeToggle } from "@konnyaku/ui/kk/theme-toggle";

/** Icons are named (not passed as components) so server layouts can describe the nav. */
const icons = {
  home: Home,
  leads: PhoneCall,
  students: GraduationCap,
  billing: Receipt,
  classes: CalendarDays,
  programs: Layers,
  staff: Users,
  branches: Building2,
  payroll: Wallet,
  book: BookOpen,
  attendance: ClipboardList,
  privacy: ShieldCheck,
} satisfies Record<string, LucideIcon>;

export type IconName = keyof typeof icons;

export type NavLink = {
  href: string;
  label: string;
  icon?: IconName;
  /** A page under another root layout (e.g. public pages from a signed-in area): plain <a>, full navigation. */
  external?: boolean;
  /** Active only on this exact path, not on its sub-pages (the home link of an area, e.g. /ops). */
  exact?: boolean;
};

export type ShellUser = { name: string; detail?: string };

export type ShellLabels = { theme: string; menu: string; logout: string; close?: string };

function isActive(pathname: string, { href, exact }: NavLink) {
  if (exact || href === "/") return pathname === href;
  return pathname === href || pathname.startsWith(`${href}/`);
}

function NavAnchor({ link, active, className, onNavigate }: { link: NavLink; active: boolean; className?: string; onNavigate?: () => void }) {
  const Icon = link.icon ? icons[link.icon] : null;
  const content = (
    <>
      {Icon && <Icon className="size-4 shrink-0" />}
      <span className="truncate">{link.label}</span>
    </>
  );
  const classes = cn(
    "relative z-10 flex items-center gap-2 rounded-md px-3 py-2 text-sm font-medium transition-colors",
    active ? "text-primary" : "text-muted-foreground hover:text-foreground",
    className,
  );
  return link.external ? (
    <a href={link.href} className={classes} data-value={link.href} onClick={onNavigate}>{content}</a>
  ) : (
    <Link href={link.href} className={classes} data-value={link.href} aria-current={active ? "page" : undefined} onClick={onNavigate}>{content}</Link>
  );
}

function UserMenu({ user, logoutAction, labels }: { user: ShellUser; logoutAction: string; labels: ShellLabels }) {
  const initials = user.name.split(/\s+/).filter(Boolean).slice(-2).map((p) => p[0]?.toUpperCase()).join("");
  return (
    <DropdownMenu>
      <DropdownMenuTrigger
        className="flex items-center gap-2 rounded-full border bg-card py-1 pr-3 pl-1 text-sm shadow-xs transition-colors hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none"
        aria-label={labels.menu}
      >
        <span className="grid size-7 place-items-center rounded-full bg-primary text-xs font-semibold text-primary-foreground">{initials}</span>
        <span className="hidden max-w-40 truncate font-medium sm:inline">{user.name}</span>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-60">
        <DropdownMenuLabel className="space-y-0.5">
          <span className="block truncate">{user.name}</span>
          {user.detail && <span className="block truncate text-xs font-normal text-muted-foreground">{user.detail}</span>}
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        <form method="post" action={logoutAction}>
          <button
            type="submit"
            className="flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-sm text-destructive hover:bg-destructive/10"
          >
            <LogOut className="size-4" />
            {labels.logout}
          </button>
        </form>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/**
 * Top bar for the public site and the learner / teacher apps: brand, nav (animated hover highlight),
 * theme switch, user menu; the nav collapses into a sheet on phones.
 */
export function TopBar({ brand, links, user, logoutAction, labels, actions }: {
  brand: ReactNode;
  links: NavLink[];
  user?: ShellUser;
  logoutAction?: string;
  labels: ShellLabels;
  actions?: ReactNode;
}) {
  const pathname = usePathname();
  const [open, setOpen] = useState(false);
  return (
    <header className="sticky top-0 z-40 border-b bg-background/80 backdrop-blur-md supports-[backdrop-filter]:bg-background/65">
      <div className="mx-auto flex h-16 max-w-6xl items-center gap-4 px-4 sm:px-6">
        {links.length > 0 && (
          <Sheet open={open} onOpenChange={setOpen}>
            <SheetTrigger className="-ml-2 grid size-9 place-items-center rounded-md hover:bg-accent md:hidden" aria-label={labels.menu}>
              <Menu className="size-5" />
            </SheetTrigger>
            <SheetContent side="left" className="w-72 p-4" closeLabel={labels.close}>
              <SheetTitle className="mb-2">{brand}</SheetTitle>
              <nav className="grid gap-1">
                {links.map((link) => (
                  <NavAnchor key={link.href} link={link} active={isActive(pathname, link)} onNavigate={() => setOpen(false)} />
                ))}
              </nav>
            </SheetContent>
          </Sheet>
        )}
        <div className="shrink-0">{brand}</div>
        {links.length > 0 && (
          <nav className="hidden items-center md:flex">
            <Highlight hover controlledItems={false} className="rounded-md bg-accent" transition={{ type: "spring", stiffness: 350, damping: 35 }}>
              {links.map((link) => (
                <NavAnchor key={link.href} link={link} active={isActive(pathname, link)} />
              ))}
            </Highlight>
          </nav>
        )}
        <div className="ml-auto flex items-center gap-2">
          {actions}
          <ThemeToggle label={labels.theme} />
          {user && logoutAction && <UserMenu user={user} logoutAction={logoutAction} labels={labels} />}
        </div>
      </div>
    </header>
  );
}

/** Admin shell: sidebar with animated highlight on desktop, sheet on phones, content on the right. */
export function SidebarShell({ brand, links, user, logoutAction, labels, children }: {
  brand: ReactNode;
  links: NavLink[];
  user?: ShellUser;
  logoutAction?: string;
  labels: ShellLabels;
  children: ReactNode;
}) {
  const pathname = usePathname();
  const [open, setOpen] = useState(false);
  const nav = (onNavigate?: () => void) => (
    <Highlight hover controlledItems={false} className="rounded-md bg-accent" transition={{ type: "spring", stiffness: 350, damping: 35 }}>
      {links.map((link) => {
        const active = isActive(pathname, link);
        return (
          <NavAnchor
            key={link.href}
            link={link}
            active={active}
            onNavigate={onNavigate}
            className={cn("w-full", active && "bg-primary/10")}
          />
        );
      })}
    </Highlight>
  );
  return (
    <div className="min-h-dvh md:grid md:grid-cols-[15rem_1fr] print:block">
      <aside className="sticky top-0 hidden h-dvh flex-col gap-4 border-r bg-sidebar p-4 text-sidebar-foreground md:flex print:hidden">
        <div className="px-2 py-1">{brand}</div>
        <nav className="grid gap-0.5">{nav()}</nav>
        <div className="mt-auto flex items-center justify-between gap-2 border-t pt-4">
          {user && logoutAction ? <UserMenu user={user} logoutAction={logoutAction} labels={labels} /> : <span />}
          <ThemeToggle label={labels.theme} />
        </div>
      </aside>
      <div className="min-w-0">
        <header className="sticky top-0 z-40 flex h-14 items-center gap-3 border-b bg-background/80 px-4 backdrop-blur-md md:hidden print:hidden">
          <Sheet open={open} onOpenChange={setOpen}>
            <SheetTrigger className="-ml-1 grid size-9 place-items-center rounded-md hover:bg-accent" aria-label={labels.menu}>
              <Menu className="size-5" />
            </SheetTrigger>
            <SheetContent side="left" className="w-72 p-4" closeLabel={labels.close}>
              <SheetTitle className="mb-2">{brand}</SheetTitle>
              <nav className="grid gap-0.5">{nav(() => setOpen(false))}</nav>
            </SheetContent>
          </Sheet>
          {brand}
          <div className="ml-auto flex items-center gap-1">
            <ThemeToggle label={labels.theme} />
            {user && logoutAction && <UserMenu user={user} logoutAction={logoutAction} labels={labels} />}
          </div>
        </header>
        <main className="mx-auto w-full max-w-6xl px-4 py-6 sm:px-8 sm:py-8">{children}</main>
      </div>
    </div>
  );
}

