import { StaffAccountPage } from "../../_account/account-page";

export default async function TeachAccountPage({ searchParams }: { searchParams: Promise<{ required?: string }> }) {
  const { required } = await searchParams;
  return <StaffAccountPage home="/teach" required={required === "1"} />;
}
