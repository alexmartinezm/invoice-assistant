/**
 * One place for money and dates, matching the formats the system prompt asks the assistant for:
 * amounts as €1,240.50 and dates as dd/mm/yyyy. The chat and the tables must not disagree.
 */
const money = new Intl.NumberFormat('en-GB', {
  style: 'currency',
  currency: 'EUR',
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});

const compactMoney = new Intl.NumberFormat('en-GB', {
  style: 'currency',
  currency: 'EUR',
  notation: 'compact',
  maximumFractionDigits: 1,
});

export const formatMoney = (amount: number) => money.format(amount);

export const formatMoneyCompact = (amount: number) => compactMoney.format(amount);

export function formatDate(isoDate: string): string {
  const [year, month, day] = isoDate.slice(0, 10).split('-');
  return `${day}/${month}/${year}`;
}
