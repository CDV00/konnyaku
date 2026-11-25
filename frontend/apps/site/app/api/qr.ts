import QRCode from "qrcode";

/** QR code of an otpauth:// link as an SVG data URL, drawn on the server so the browser loads no extra library. */
export async function qrDataUrl(uri: string): Promise<string> {
  const svg = await QRCode.toString(uri, { type: "svg", margin: 1, errorCorrectionLevel: "M" });
  return `data:image/svg+xml;base64,${Buffer.from(svg).toString("base64")}`;
}
