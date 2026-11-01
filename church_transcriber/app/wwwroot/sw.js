// Minimal service worker so the app can be installed to the home screen. Always uses the network (live data).
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', e => e.waitUntil(self.clients.claim()));
self.addEventListener('fetch', () => {});
