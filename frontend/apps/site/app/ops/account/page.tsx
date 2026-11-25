import { StaffAccountPage } from "../../_account/account-page";

export default async function OpsAccountPage({ searchParams }: { searchParams: Promise<{ required?: string }> }) {
  const { required } = await searchParams;
  return <StaffAccountPage home="/ops" required={required === "1"} />;
}
