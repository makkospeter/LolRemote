import { chromium, devices } from 'playwright';

const base = process.argv[2] || 'http://127.0.0.1:8765';
const shots = process.argv[3] || '.';
const results = [];
const check = (name, ok) => { results.push(`${ok ? 'PASS' : 'FAIL'}  ${name}`); };

const browser = await chromium.launch();
const context = await browser.newContext({ ...devices['iPhone 13'] });
const page = await context.newPage();
const errors = [];
page.on('pageerror', e => errors.push(e.message));
page.on('console', m => { if (m.type() === 'error' && !m.text().includes('404')) errors.push(m.text()); });

const setPhase = name => fetch(`${base}/__test/phase?name=${name}`);
const shot = name => page.screenshot({ path: `${shots}/${name}.png`, fullPage: true });

await setPhase('Lobby');
await page.goto(base);
await page.waitForSelector('#code-input');
check('Párosító képernyő megjelenik', await page.isVisible('text=Párosítás'));
await shot('01-parositas');

await page.fill('#code-input', '999999');
await page.click('button[type=submit]');
await page.waitForSelector('text=Hibás vagy régi kód');
check('Hibás kód hibaüzenet', true);
await shot('02-hibas-kod');

await page.fill('#code-input', '123456');
await page.click('button[type=submit]');
await page.waitForSelector('text=Lobby');
check('Helyes kóddal csatlakozik, lobby látszik', true);
await shot('03-lobby');

await page.reload();
await page.waitForSelector('text=Lobby');
check('Újratöltés után emlékszik a kódra', true);

await page.click('[data-action=toggle-auto-accept]');
await page.waitForSelector('[data-action=toggle-auto-accept][aria-checked=true]');
check('Auto-accept kapcsoló bekapcsol', true);
await page.click('[data-action=toggle-auto-accept]');
await page.waitForSelector('[data-action=toggle-auto-accept][aria-checked=false]');
check('Auto-accept kapcsoló kikapcsol', true);
await page.click('[data-action=toggle-sound]');
check('Hang kapcsoló helyben kikapcsol', await page.isVisible('[data-action=toggle-sound][aria-checked=false]'));
await page.click('[data-action=toggle-sound]');
await shot('03b-beallitasok');

await setPhase('ReadyCheck');
await page.waitForSelector('.accept-btn');
check('Ready check gomb megjelenik', true);
await shot('04-ready-check');
await page.click('.accept-btn', { force: true });
await page.waitForSelector('text=TE JÖSSZ · BAN');
check('Elfogadás után champ select, ban kör', true);
await shot('05-ban-kor');

await page.click('[data-action=open-champs]');
await page.waitForSelector('.champ');
await page.fill('[data-search]', 'yas');
const yasuoOnly = await page.$$eval('.champ', els => els.map(e => e.textContent.trim()));
check(`Keresés szűr (yas → ${yasuoOnly.join(',')})`, yasuoOnly.length === 1 && yasuoOnly[0] === 'Yasuo');
await shot('06-kereses');
await page.click('.champ');
await page.waitForSelector('.confirm >> text=Yasuo');
check('Champion választás után megerősítő ablak jön (BAN gombbal)', await page.isVisible('[data-action=confirm-lock] >> text=BAN'));
await shot('06b-megerosites');
await page.click('.confirm [data-action=close-sheet]');
await page.waitForSelector('.turn-choice >> text=Yasuo');
check('Hover után a kijelölt champion látszik', true);

await page.click('[data-action=open-champs]');
await page.waitForSelector('.champ');
await page.click('.sheet-backdrop', { position: { x: 20, y: 20 } });
await page.waitForSelector('#sheet', { state: 'hidden' });
check('Keresés bezárása után a kijelölés megmarad', await page.isVisible('.turn-choice >> text=Yasuo'));

await page.click('[data-action=lock]');
await page.waitForSelector('text=TE JÖSSZ · PICK');
check('Ban lockolás után pick kör', true);
await page.waitForSelector('button.counter-row');
const counterNames = await page.$$eval('.counters .counter-name', els => els.map(e => e.firstChild.textContent.trim()));
check(`Bannolt (Yasuo) és foglalt champion nincs a counterek között (${counterNames.join(',')})`, !counterNames.includes('Yasuo') && !counterNames.includes('Teemo') && !counterNames.includes('Lux'));
await shot('07a-counter-kattinthato');
await page.click('button.counter-row >> nth=0');
await page.waitForSelector('.confirm [data-action=confirm-lock]');
check('Counterre koppintva kijelöli és rákérdez a lockra', await page.isVisible('.confirm >> text=LOCK IN'));
await page.click('.confirm [data-action=close-sheet]');
await page.click('[data-action=open-champs]');
await page.waitForSelector('.champ');
await page.fill('[data-search]', 'ah');
await page.click('.champ >> text=Ahri');
await page.waitForSelector('.confirm >> text=Ahri');
await shot('07-pick-hover');
await page.click('[data-action=confirm-lock]');
await page.waitForSelector('text=MINDEN PICK KÉSZ');
check('Pick lock után finalization', true);
check('Bejövő csere kérés kártya', await page.isVisible('text=champion cserét kér'));
await shot('08-finalizacio');

await page.waitForSelector('.counters .counter-row');
check('Sávellenfél tipp (Lux) a chipen', await page.isVisible('.chip.on >> text=Lux'));
check(`Matchup: top 5 counter látszik (${await page.locator('.counters .counter-row').count()})`, await page.locator('.counters .counter-row').count() === 5);
check('Nincs OP.GG-re átdobó link', (await page.locator('.counters a').count()) === 0);
check('Kijelöl gomb nincs, ha nem én pickelek', (await page.locator('[data-action=hover-counter]').count()) === 0);
await shot('08c-matchup');
await page.click('[data-action=toggle-counters]');
await page.waitForFunction(() => document.querySelectorAll('.counters .counter-row').length > 5);
const fullCount = await page.locator('.counters .counter-row').count();
const allNames = await page.$$eval('.counters .counter-name', els => els.map(e => e.firstChild.textContent.trim()));
check(`"Összes mutatása" lenyitja a teljes listát (${fullCount}), bannolt/foglalt nélkül`, !allNames.some(n => ['Yasuo', 'Teemo', 'Annie', 'Lux', 'Ezreal', 'Thresh'].includes(n)));
await shot('08d-matchup-osszes');
await page.click('[data-action=toggle-counters]');
await page.waitForFunction(() => document.querySelectorAll('.counters .counter-row').length === 5);
check('"Kevesebb" visszacsukja', true);
await page.click('.chip >> text=Te:');
await page.waitForSelector('.counter-label >> text=Neked nehéz');
check('Saját championnál csak a nehéz ellenfelek vannak', !(await page.isVisible('text=könnyű')));
await page.click('.chip >> text=Teemo');
await page.waitForSelector('.lane-info >> text=Teemo');
check('Ellenfél chipre koppintva az ajánlás arra a matchupra vált', true);
await page.click('.chip >> text=Lux');
await page.waitForSelector('.lane-info >> text=Lux');
check('Sablon csempe: még nincs mentve', await page.isVisible('.tile.template >> text=Nincs mentve'));
await page.click('[data-action=template-save]');
await page.waitForSelector('[data-action=template-apply]');
check('Sablon mentése után megjelenik az Alkalmaz gomb', true);
await page.click('[data-action=template-apply]');
await page.waitForSelector('text=Sablon alkalmazva');
check('Sablon alkalmazása visszajelzést ad', true);
await shot('08b-sablon');

await page.click('[data-action=open-spell][data-slot="2"]');
await page.waitForSelector('.option.rec >> text=Flash + Ignite');
check('Spell lap: ajánlott párok meccsszámmal', await page.isVisible('.option.rec >> text=4146 meccs'));
await shot('09-spell-lap');
await page.click('.option:not(.rec) >> text=Teleport');
await page.waitForSelector('.tile >> text=Teleport');
check('F spell csere Teleportra', true);

await page.click('[data-action=open-spell][data-slot="1"]');
await page.click('.option:not(.rec) >> text=Teleport');
await page.waitForFunction(() => {
  const vals = [...document.querySelectorAll('.tile .val')].map(e => e.textContent);
  return vals[0] === 'Teleport' && vals[1] === 'Flash';
});
check('D spellre a másik slot spelljét választva a kettő helyet cserél', true);

await page.locator('[data-action=open-runes]').scrollIntoViewIfNeeded();
const scrollBefore = await page.evaluate(() => window.scrollY);
await page.click('[data-action=open-runes]');
await page.waitForSelector('.option.rec');
check(`Rúna lap: 3 OP.GG ajánlás meccsszámmal (${await page.locator('.option.rec').count()})`, await page.locator('.option.rec').count() === 3 && await page.isVisible('text=2439 meccs'));
check('Nincs "több ajánlás" gomb', (await page.locator('[data-action=more-recs]').count()) === 0);
check('Nyitott lapnál a háttér nem görgethető', await page.evaluate(() => document.body.classList.contains('locked') && getComputedStyle(document.body).position === 'fixed'));
const listScroll = await page.evaluate(() => { const l = document.querySelector('.sheet .list'); l.style.maxHeight = '200px'; l.scrollTop = 120; return l.scrollTop > 0 && getComputedStyle(l).overflowY === 'auto'; });
check('A rúnalista saját maga görgethető', listScroll);
await shot('09b-runa-ajanlasok');
await page.click('.option.rec >> nth=1');
await page.waitForSelector('text=Rúnaoldal beállítva');
check('Ajánlott rúnaoldal alkalmazása', true);
check(`Bezárás után a háttér görgetése visszaáll (${scrollBefore})`, await page.evaluate(y => !document.body.classList.contains('locked') && Math.abs(window.scrollY - y) < 2, scrollBefore));

await page.click('[data-action=open-runes]');
await page.waitForSelector('.option >> text=ADC - Lethal Tempo');
await page.click('.option >> text=ADC - Lethal Tempo');
await page.waitForSelector('.tile >> text=ADC - Lethal Tempo');
check('Rúnaoldal csere', true);

await page.click('[data-action=open-swaps]');
await page.waitForSelector('.option >> text=Champion csere');
await shot('10-csere-lap');
await page.click('.sheet-backdrop', { position: { x: 20, y: 20 } });

await page.click('[data-action=apply-quick]');
await page.waitForSelector('text=Rúnaoldal beállítva');
check('Ajánlott (rúna + spell) gyors beállítás', true);

await page.click('[data-action=swap-accept]');
await page.waitForFunction(() => !document.querySelector('.incoming'));
check('Bejövő csere elfogadása', await page.isVisible('.member.me >> text=Jinx'));
await shot('11-csere-utan');

await setPhase('InProgress');
await page.waitForSelector('text=Játék folyamatban');
check('Játék közben nincs champ select / rúna', !(await page.isVisible('[data-action=open-runes]')));
await shot('12-jatekban');

const commands = await (await fetch(`${base}/__test/commands`)).json();
const sequence = commands.map(c => c.cmd).filter(c => !['counters', 'build', 'rune-recommendations'].includes(c)).join(',');
check(`Parancsok sorrendje: ${sequence}`, sequence === 'setting,setting,accept,champions,hover,champions,lock,hover,champions,hover,lock,template-save,template-apply,spells,spells,rune-apply-recommended,rune,spells,rune-apply-recommended,swap');
const spellCmds = commands.filter(c => c.cmd === 'spells').map(c => `${c.spell1}/${c.spell2}`);
check(`Spell parancsok: ${spellCmds.join(' ')}`, spellCmds.join(' ') === '4/12 12/4 14/4');
const builds = commands.filter(c => c.cmd === 'build').map(c => c.opponentId);
check(`Build kérések (ellenfél): ${builds.join(',')}`, builds.includes(17) && builds.includes(99));

const fresh = await browser.newContext({ ...devices['iPhone 13'] });
const qrPage = await fresh.newPage();
await setPhase('Lobby');
await qrPage.goto(`${base}/?k=123456`);
await qrPage.waitForSelector('text=Lobby');
check('QR-linkkel (?k=kód) magától párosít', qrPage.url().includes('k=123456'));
await fresh.close();
check(`Nincs JS hiba (${errors.join(' | ') || 'nincs'})`, errors.length === 0);

console.log(results.join('\n'));
await browser.close();
process.exit(results.some(r => r.startsWith('FAIL')) ? 1 : 0);
