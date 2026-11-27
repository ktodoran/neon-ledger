/** A fresh idempotency key that reads well on screen, such as "deposit-3fa9c1". */
export function newKey(prefix) {
  const bytes = crypto.getRandomValues(new Uint8Array(3));
  return `${prefix}-${[...bytes].map((byte) => byte.toString(16).padStart(2, '0')).join('')}`;
}
