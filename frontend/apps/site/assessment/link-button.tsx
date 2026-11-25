"use client";

import type { ComponentProps } from "react";
import Link from "next/link";
import { buttonVariants } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { cn } from "@konnyaku/ui/lib/utils";

type Variant = NonNullable<Parameters<typeof buttonVariants>[0]>;

/** A link styled as a button, usable from server components (the button styles live in a client module). */
export function LinkButton({ variant, size, className, ...props }: ComponentProps<typeof Link> & { variant?: Variant["variant"]; size?: Variant["size"] }) {
  return <Link className={cn(buttonVariants({ variant, size }), className)} {...props} />;
}
