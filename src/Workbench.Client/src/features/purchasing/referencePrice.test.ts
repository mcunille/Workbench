import { formatReferencePrice } from './referencePrice';
it.each([['125.5000', '125.50'], ['125.5012', '125.5012'], ['0.0000', '0.00'], ['999999999999999.9999', '999999999999999.9999'], [null, null], ['invalid', 'invalid']])('formats %s without losing precision', (input, expected) => {
  // GIVEN an exact decimal or an unknown/invalid local value.
  // WHEN presenting the reference price THEN only unnecessary zeroes are removed.
  expect(formatReferencePrice(input)).toBe(expected);
});
