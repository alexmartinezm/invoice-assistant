import type { AppConfig } from './api/types';

/**
 * One place for money and dates. The server decides the currency and locale (`Invoicing:*`), and
 * both this module and the assistant's formatting instruction are rendered from that same
 * configuration — otherwise the chat and the table beside it disagree about the same number.
 */
const fallback: AppConfig = {
  currency: 'EUR',
  locale: 'en-GB',
  taxLabel: 'VAT',
  defaultVatRate: 0.21,
  accountantMarkPaidLimit: 1000,
};

let active: AppConfig = fallback;
let money = buildMoney(active);
let compactMoney = buildCompactMoney(active);
let date = buildDate(active);

function buildMoney(config: AppConfig) {
  return new Intl.NumberFormat(config.locale, {
    style: 'currency',
    currency: config.currency,
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });
}

function buildCompactMoney(config: AppConfig) {
  return new Intl.NumberFormat(config.locale, {
    style: 'currency',
    currency: config.currency,
    notation: 'compact',
    maximumFractionDigits: 1,
  });
}

function buildDate(config: AppConfig) {
  return new Intl.DateTimeFormat(config.locale, {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
  });
}

/**
 * Called once at boot, before the first render, so no amount is ever painted in the wrong
 * currency and then corrected.
 */
export function configureFormatting(config: AppConfig): void {
  active = config;
  money = buildMoney(config);
  compactMoney = buildCompactMoney(config);
  date = buildDate(config);
}

export const appConfig = (): AppConfig => active;

export const formatMoney = (amount: number) => money.format(amount);

export const formatMoneyCompact = (amount: number) => compactMoney.format(amount);

/** Takes the API's `yyyy-MM-dd`, which is a calendar date with no timezone to shift it. */
export function formatDate(isoDate: string): string {
  const [year, month, day] = isoDate.slice(0, 10).split('-').map(Number);
  return date.format(new Date(year!, month! - 1, day!));
}
