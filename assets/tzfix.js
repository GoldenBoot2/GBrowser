// Time-zone names as IP-location services and other browsers write them (IANA's current names). Chromium's ICU still
// reports old aliases such as "Asia/Calcutta"; BrowserScan compares the name with the IP's "Asia/Kolkata" and took
// 10 % off for "different time zones" (19-11-2026). Same zone, same offset: only the name changes, everywhere a page
// can read it. Shared by the page script (gbab.js, /*TZFIX*/) and workers (UaOverride.cs runs it before they start).
var TZ_NAMES = {'Asia/Calcutta': 'Asia/Kolkata', 'Asia/Katmandu': 'Asia/Kathmandu', 'Asia/Saigon': 'Asia/Ho_Chi_Minh',
  'Asia/Rangoon': 'Asia/Yangon', 'Europe/Kiev': 'Europe/Kyiv', 'Atlantic/Faeroe': 'Atlantic/Faroe',
  'America/Godthab': 'America/Nuuk', 'Pacific/Ponape': 'Pacific/Pohnpei', 'Pacific/Truk': 'Pacific/Chuuk',
  'Pacific/Enderbury': 'Pacific/Kanton', 'Asia/Dacca': 'Asia/Dhaka', 'Asia/Thimbu': 'Asia/Thimphu',
  'Asia/Ulan_Bator': 'Asia/Ulaanbaatar', 'America/Buenos_Aires': 'America/Argentina/Buenos_Aires',
  'America/Indianapolis': 'America/Indiana/Indianapolis', 'America/Louisville': 'America/Kentucky/Louisville',
  'America/Catamarca': 'America/Argentina/Catamarca', 'America/Cordoba': 'America/Argentina/Cordoba',
  'America/Jujuy': 'America/Argentina/Jujuy', 'America/Mendoza': 'America/Argentina/Mendoza'};
