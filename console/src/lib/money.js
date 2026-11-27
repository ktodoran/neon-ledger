// Amounts are whole numbers of minor units, as in the API: 25000 is $250.00.

const formatters = new Map();

function formatterFor(currency) {
  if (!formatters.has(currency)) {
    formatters.set(currency, new Intl.NumberFormat('en-US', { style: 'currency', currency }));
  }
  return formatters.get(currency);
}

export function formatMoney(minorUnits, currency = 'USD') {
  return formatterFor(currency).format(minorUnits / 100);
}

/**
 * Reads what a person typed, such as "120", "$1,200.50" or "0.5", into minor
 * units. Returns null for anything that is not a positive amount with at most
 * two decimal places: money is never rounded on the way in.
 */
export function parseMoney(text) {
  const cleaned = String(text).replace(/[$,\s]/g, '');
  if (!/^\d+(\.\d{1,2})?$/.test(cleaned)) return null;
  const [whole, fraction = ''] = cleaned.split('.');
  const minorUnits = Number(whole) * 100 + Number(fraction.padEnd(2, '0'));
  return Number.isSafeInteger(minorUnits) && minorUnits > 0 ? minorUnits : null;
}
