// Workers (dedicated, shared, service): the same time-zone names as pages (assets/tzfix.js). UaOverride.cs runs this
// in each worker before its own code starts, wrapped in a function with tzfix.js, so nothing is left on its global.
var FTS = Function.prototype.toString, apply = Reflect.apply, natives = new WeakMap(), dP = Object.defineProperty, gD = Object.getOwnPropertyDescriptor;
function hide(w, o) {
  natives.set(w, o);
  try { dP(w, 'name', {value: o.name, configurable: true}); dP(w, 'length', {value: o.length, configurable: true}); } catch (e) {}
  return w;
}
// toString of a replaced function shows the browser's own (a worker has no frames to read a clean one from)
var ts = hide({toString() { return apply(FTS, natives.has(this) ? natives.get(this) : this, []); }}.toString, FTS);
dP(Function.prototype, 'toString', {value: ts, writable: true, enumerable: false, configurable: true});
var fix = function (z) { return typeof z === 'string' && Object.prototype.hasOwnProperty.call(TZ_NAMES, z) ? TZ_NAMES[z] : z; };
var brk = function (s) { return typeof s === 'string' ? s.replace(/\[([A-Za-z_\/+-]+)\]/g, function (m, z) { return '[' + fix(z) + ']'; }) : s; };
function rv(o, n, mk) {
  try { var d = o && gD(o, n); if (!d || typeof d.value !== 'function') return;
    dP(o, n, {value: hide(mk(d.value), d.value), writable: d.writable, enumerable: d.enumerable, configurable: d.configurable}); } catch (e) {}
}
function rg(o, n, mk) {
  try { var d = o && gD(o, n); if (!d || typeof d.get !== 'function') return;
    dP(o, n, {get: hide(mk(d.get), d.get), set: d.set, enumerable: d.enumerable, configurable: d.configurable}); } catch (e) {}
}
var I = self.Intl;
rv(I && I.DateTimeFormat && I.DateTimeFormat.prototype, 'resolvedOptions', function (f) {
  return {resolvedOptions() { var r = apply(f, this, arguments); try { if (r && typeof r.timeZone === 'string') r.timeZone = fix(r.timeZone); } catch (e) {} return r; }}.resolvedOptions;
});
rv(I, 'supportedValuesOf', function (f) {
  return {supportedValuesOf(k) { var r = apply(f, this, arguments); try { if (k === 'timeZone') { for (var i = 0; i < r.length; i++) r[i] = fix(r[i]); r.sort(); } } catch (e) {} return r; }}.supportedValuesOf;
});
var T = self.Temporal;
if (T) {
  rv(T.Now, 'timeZoneId', function (f) { return {timeZoneId() { return fix(apply(f, this, arguments)); }}.timeZoneId; });
  var ZP = T.ZonedDateTime && T.ZonedDateTime.prototype;
  rg(ZP, 'timeZoneId', function (g) { return {get timeZoneId() { return fix(apply(g, this, [])); }}.__lookupGetter__('timeZoneId'); });
  rv(ZP, 'toString', function (f) { return {toString() { return brk(apply(f, this, arguments)); }}.toString; });
  rv(ZP, 'toJSON', function (f) { return {toJSON() { return brk(apply(f, this, arguments)); }}.toJSON; });
}
