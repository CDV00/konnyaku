const units = ["byte", "kilobyte", "megabyte", "gigabyte", "terabyte"] as const;

/** 1 536 → "1.5 kB" in the user's locale (steps of 1024, as the quota is counted). */
export function formatBytes(bytes: number, locale: string): string {
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return new Intl.NumberFormat(locale, {
    style: "unit",
    unit: units[unit],
    unitDisplay: "short",
    maximumFractionDigits: unit === 0 ? 0 : 1,
  }).format(value);
}
