type IconName =
  | 'menu'
  | 'system'
  | 'sun'
  | 'moon'
  | 'grid'
  | 'list'
  | 'inventory'
  | 'cart'
  | 'account'
  | 'sign-out'
  | 'administration'
  | 'image'
  | 'location'
  | 'plus'
  | 'chevron'
  | 'back';

const paths: Record<IconName, string> = {
  menu: 'M3 6h18 M3 12h18 M3 18h18',
  system: 'M3 4h18v13H3z M8 21h8 M12 17v4',
  'sign-out': 'M9 4H4v16h5 M10 12h11 M17 8l4 4-4 4',
  sun: 'M16 12a4 4 0 1 1-8 0 4 4 0 0 1 8 0 M12 2v2 M12 20v2 M2 12h2 M20 12h2 M5 5l1.5 1.5 M17.5 17.5 19 19 M5 19l1.5-1.5 M17.5 6.5 19 5',
  moon: 'M20.5 13a8.5 8.5 0 0 1-9.5-9.5A8.5 8.5 0 1 0 20.5 13z',
  grid: 'M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z',
  list: 'M8 5h13 M8 12h13 M8 19h13 M3 5h.01 M3 12h.01 M3 19h.01',
  inventory: 'M4 8h16v12H4z M3 4h18v4H3z M9 12h6',
  cart: 'M2 3h3l3 12h11l3-9H6 M8 15l-1 3h13 M10 21h.01 M18 21h.01',
  account: 'M20 21v-2a8 8 0 0 0-16 0v2 M16 7a4 4 0 1 1-8 0 4 4 0 0 1 8 0',
  administration: 'M12 3 3 7v5c0 5 9 9 9 9s9-4 9-9V7z M8 12l3 3 5-6',
  image:
    'M4 3h16a1 1 0 0 1 1 1v16a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1z M3 16l5-5 5 5 3-3 5 5 M17 7h.01',
  location:
    'M20 10c0 6-8 11-8 11S4 16 4 10a8 8 0 1 1 16 0z M15 10a3 3 0 1 1-6 0 3 3 0 0 1 6 0',
  plus: 'M12 5v14 M5 12h14',
  chevron: 'm9 5 7 7-7 7',
  back: 'm15 5-7 7 7 7',
};

export function Icon({ name }: { name: IconName }) {
  return (
    <svg
      className={`icon icon-${name}`}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.6"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      <path d={paths[name]} />
    </svg>
  );
}
