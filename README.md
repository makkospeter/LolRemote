# LolRemote

League of Legends champ select a telefonodról. Elmehetsz a géptől, és a telefonról:

- elfogadod a meccset (vagy hagyod, hogy magától elfogadja),
- bannolsz, kijelölsz és lockolsz championt,
- beállítod a summonereket és a rúnát (OP.GG ajánlással, meccsszámmal),
- látod, kik jók az ellenfél pickje ellen,
- elfogadod vagy kéred a champion- és pick-cseréket.

Mindent te döntesz el: a program csak azt csinálja, amire a telefonon rányomsz. Kivétel az automatikus elfogadás és a rúnasablon, de ezeket neked kell bekapcsolnod.

## Telepítés (1 perc)

1. Töltsd le a **LolRemote.exe** fájlt:
   **https://github.com/makkospeter/LolRemote/releases/latest/download/LolRemote.exe**
2. Indítsd el. Megnyílik a LolRemote ablaka (nem parancssor). Ha a Windows azt írja, hogy „A Windows megvédte a számítógépet”, kattints a **További információ → Futtatás mindenképp** lehetőségre. Ez azért jelenik meg, mert a program nincs fizetős tanúsítvánnyal aláírva.
3. Ha a Windows tűzfal rákérdez, nyomd meg az **Engedélyezés** gombot. Enélkül a telefon nem éri el a gépet.
4. Az ablak bal oldalán látsz egy QR-kódot. Olvasd be a telefon **Kamera** appjával.
5. iPhone-on a Safariban: **Megosztás → Főképernyőhöz adás**. Androidon a Chrome menüben: **Hozzáadás a kezdőképernyőhöz**. Így appként a kezdőképernyőn lesz.

Ezután csak indítsd el a LolRemote.exe-t a gépen, és nyisd meg a telefonon a LolRemote ikont. A program magától megtalálja a League klienst.

> A telefon és a gép legyen ugyanazon a WiFi-n / routeren (vendég WiFi nem jó).

## Mit látsz a telefonon

| Rész | Mit tud |
|---|---|
| Ready check | Nagy ELFOGAD gomb visszaszámlálással, hang és rezgés |
| Te jössz | Champion keresés, hover, LOCK IN / BAN |
| Matchup | Az ellenfél pickjei ellen jó championok (OP.GG, nyerési arány + meccsszám). Az „Összes mutatása” lenyitja a teljes listát. A tipp szerinti sávellenfelet „sáv” jelöli. |
| ⚡ Ajánlott beállítása | Egy gombbal beállítja a legnépszerűbb summonereket és rúnát a sávellenfél ellen |
| Spellek / Rúna | OP.GG ajánlott párok és rúnaoldalak meccsszámmal, plusz a saját oldalaid |
| Sablon | Championonként elmentheted a rúna + spell párost; lock után magától beállítja |
| Csere | Champion- és pick-sorrend csere kérése, elfogadása |

## Natív iPhone app (haladóknak, nem kötelező)

A kezdőképernyős app mindenre elég. Ha mégis „igazi” appot szeretnél, a `mobile` mappában van egy Expo-s app QR-olvasóval:

1. Telepítsd a gépre a Node.js LTS-t (https://nodejs.org), az iPhone-ra pedig az **Expo Go** appot az App Store-ból.
2. Jobb klikk a `mobile\setup-ios.ps1` fájlon → **Futtatás PowerShell-lel**.
3. Olvasd be az iPhone kamerájával a megjelenő Expo QR-kódot. Az app megnyílik az Expo Go-ban, ott nyomd meg a **QR-kód beolvasása** gombot, és olvasd be a LolRemote QR-kódját.

Önálló, App Store nélküli telepítéshez (TestFlight vagy ad-hoc) Apple fejlesztői fiók kell (99 USD/év). Ekkor az `eas build -p ios` paranccsal készíthető.

## Gyakori kérdések

**Ban jár érte?** Garanciát senki nem adhat rá, de a program csak a League kliens helyi felületét (LCU API) használja, ugyanazt, amit a Blitz, a Porofessor vagy az u.gg app. A játékhoz nem nyúl, és helyetted nem dönt: mindent te nyomsz meg a telefonon. Az automatikus elfogadás alapból ki van kapcsolva.

**Nem nyílik meg a telefonon.** Nézd meg, hogy ugyanazon a hálózaton vagytok-e, és hogy a tűzfalnál az Engedélyezést nyomtad-e. Ha elnyomtad: Windows keresőben „Alkalmazás engedélyezése a Windows tűzfalon” → LolRemote → Magánhálózat legyen bepipálva. Ha a gép új IP-címet kapott, olvasd be újra a QR-kódot (az ablakban a „QR-kód mutatása” gombbal előhozod).

**Rövid cím:** a QR-kód helyett beírhatod a telefonon a `http://lolremote.local` címet is (ha a hálózat engedi), majd a 6 jegyű kódot.

**Honnan jönnek a statisztikák?** Az OP.GG hivatalos adatszolgáltatásából (ranked, minden tier). A program 30 percig gyorsítótárazza őket.

## Fejlesztőknek

- `src/LolRemote.Companion`: a PC-s program (.NET 9, C#, saját ablakkal). Benne van a telefonos weboldal is (`wwwroot`, a kiadásban az exe-be ágyazva).
- `src/LolRemote.MockLcu`: kamu League kliens, League nélküli fejlesztéshez és teszteléshez.
- `tools/dev`: `start-mock.cmd` és `start-dev-mock.cmd` (fejlesztés League nélkül), `start-dev.cmd` (valódi klienssel, diagnosztikai mentéssel), `selftest.cmd` / `selftest-practice.cmd` (automata teszt a mock ellen).
- `tools/web-test`: a telefonos felület tesztje Playwrighttal (`node fake-companion.mjs <wwwroot> 8765`, majd `node ui-test.mjs`).
- `build-release.cmd`: elkészíti a `release\LolRemote.exe`-t. Egy `v*` tag pusholásakor a GitHub Actions is elkészíti, és feltölti a Release-hez.

Kapcsolók: `--mock`, `--lockfile <út>`, `--debug-dir <mappa>`, `--console` (régi szöveges felület), `--selftest`. Napló: `%APPDATA%\LolRemote\lolremote.log`.

A PC-s ablak Windows Forms + WebView2 (a Windows 10/11 része), a felülete ugyanaz a weboldal, amit a telefon is használ (`wwwroot/desktop.html`).

A LolRemote nem a Riot Games terméke, és a Riot Games nem támogatja vagy hagyta jóvá. A League of Legends a Riot Games, Inc. védjegye.
