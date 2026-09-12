import { useLayoutEffect, useRef, useState } from 'react';
import { FloatingField } from '../../FloatingField';
import { formatReferencePrice } from './referencePrice';

function needsPrecision(value: string | null) {
  return value !== null && (!/^\d+(\.\d{0,4})?$/.test(value) || /\.\d{2}\d*[1-9]\d*$/.test(value));
}
function fromDigits(value: string): string | null {
  const digits = value.replace(/\D/g, '').replace(/^0+/, '');
  if (!/\d/.test(value)) return null;
  const padded = digits.padStart(3, '0');
  return `${padded.slice(0, -2)}.${padded.slice(-2)}`;
}
export function ReferencePriceField({ id, index, value, onChange, disabled, error }: {
  id: string; index: number; value: string | null; onChange(value: string | null): void; disabled: boolean; error?: string;
}) {
  const [requestedPrecision, setRequestedPrecision] = useState(false);
  const preciseValue = needsPrecision(value);
  const extra = requestedPrecision || preciseValue;
  const input = useRef<HTMLInputElement>(null);
  const pinCaret = (element: HTMLInputElement) => element.setSelectionRange(element.value.length, element.value.length);
  useLayoutEffect(() => {
    if (!extra && input.current === document.activeElement && input.current) pinCaret(input.current);
  });
  return <div className="po-field po-price-field">
    <FloatingField htmlFor={id} label={`Reference price ${index}`}>
      <input ref={input} id={id} value={value ?? ''} disabled={disabled} placeholder="0.00"
        inputMode={extra ? 'decimal' : 'numeric'} aria-invalid={!!error}
        aria-describedby={`${id}-help${error ? ` ${id}-error` : ''}`}
        onFocus={event => { if (!extra) pinCaret(event.currentTarget); }}
        onMouseUp={event => { if (!extra && event.currentTarget.selectionStart === event.currentTarget.selectionEnd) pinCaret(event.currentTarget); }}
        onKeyDown={event => {
          if (extra) return;
          if (['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) event.preventDefault();
          if (event.key === 'Backspace' || event.key === 'Delete') {
            event.preventDefault();
            const element = event.currentTarget;
            const allSelected = element.selectionStart === 0 && element.selectionEnd === element.value.length;
            const digits = (value ?? '').replace(/\D/g, '').replace(/^0+/, '');
            onChange(allSelected || digits.length <= 1 ? null : fromDigits(digits.slice(0, -1)));
          }
        }}
        onPaste={event => {
          event.preventDefault();
          const pasted = event.clipboardData.getData('text').trim();
          if (extra || pasted.includes('.') || !/^\d*$/.test(pasted)) {
            onChange(pasted === '' ? null : formatReferencePrice(pasted));
          } else onChange(fromDigits(pasted));
        }}
        onChange={event => {
          const text = event.target.value;
          const deleting = (event.nativeEvent as InputEvent).inputType?.startsWith('delete');
          onChange(extra ? text || null : deleting && value === '0.00' ? null
            : /^[\d.]*$/.test(text) && (text.match(/\./g)?.length ?? 0) <= 1 ? fromDigits(text) : text || null);
        }} />
    </FloatingField>
    <label className="po-precision-toggle"><input type="checkbox" checked={extra} disabled={disabled || preciseValue}
      aria-label={`Use extra precision for entry ${index}`} onChange={event => {
        setRequestedPrecision(event.target.checked);
        if (!event.target.checked) onChange(formatReferencePrice(value));
      }} />Use extra precision</label>
    <p id={`${id}-help`} className="po-price-help">{extra ? 'Type a decimal amount, up to four decimal places. Remove extra digits to return to two-decimal entry.' : 'Digits fill from the right: 1 → 0.01. Leave blank for an unknown price.'}</p>
    {error ? <p id={`${id}-error`} className="form-message error">{error}</p> : null}
  </div>;
}
