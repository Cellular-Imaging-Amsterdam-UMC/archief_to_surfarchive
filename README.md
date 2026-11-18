# SURF Archief

Windows-app om bestanden te kopiëren tussen Windows en je persoonlijke SURF Data Archive. Links open je `L:\Archief\Active`, een lokale schijf of een Windows-pad. Rechts blader je vanaf `/archive/<gebruikersnaam>`.

1. Open **SurfArchive.exe** op Windows x64 en kies **Verbinden** met je SURF Data Archive-account.
2. Kies links een bestand en rechts een doelmap. Klik op **Upload →**. Voor de andere richting kies je rechts een bestand en klik je op **← Download**.
3. Kies rechts een bestand en klik op **Archiefstatus** om de uitvoer van `dals -l -q` te zien. Is een download alleen op tape beschikbaar, haal het bestand zo nodig eerst op met `daget` via SSH en wacht op de status `DUL`.
4. Gebruik **Proefupload** om het geselecteerde Windows-bestand tijdelijk te uploaden en het daarna automatisch te laten verwijderen.

**Lokaal bewaren** versleutelt het account met Windows DPAPI voor de huidige Windows-gebruiker. Het bestand staat in `%LOCALAPPDATA%\SurfArchive\account.dpapi`. Verbreek de verbinding en gebruik **Wissen** om het bewaarde account te verwijderen.

De app gebruikt SFTP over poort 22 en accepteert de [door SURF gepubliceerde hostsleutels](https://servicedesk.surf.nl/wiki/spaces/WIKI/pages/62227258/Data+Archive+Accessing+the+service). Normale kopieën krijgen pas hun definitieve naam nadat de app de grootte heeft gecontroleerd. Bestaande bestemmingen worden niet overschreven. Bij annuleren probeert de app de tijdelijke kopie te verwijderen. Een onderbroken kopie start opnieuw vanaf het begin.

Een proefupload verifieert ook SHA-256, leest direct na de upload de archiefstatus en ruimt het testbestand en de eigen tijdelijke map op met `rm` en `rmdir`. Volg de statusregels in de app om te zien of SURF de verwijdering heeft bevestigd. Zie de [SURF DAcommands](https://servicedesk.surf.nl/wiki/spaces/WIKI/pages/166920955/Data+Archive+DA+Commands) en de [DAcommands-referentie](https://servicedesk.surf.nl/wiki/spaces/WIKI/pages/166920963/DAcommands+Documentation).

## Bouwen

Installeer de .NET 10 SDK op Windows. Bouw vanuit de projectmap:

```powershell
dotnet publish -c Release -p:RestoreLockedMode=true -o release
```

Het resultaat is `release\SurfArchive.exe`, inclusief de runtime.

Je kunt dezelfde proef ook aanroepen vanuit PowerShell met een lokaal bewaard account. De proef schrijft de resultaten naar het opgegeven logbestand en eindigt met statuscode 0 als ook het opruimen bevestigd is.

```powershell
$p = Start-Process .\release\SurfArchive.exe -ArgumentList '--proef "C:\pad\bestand.lif" "C:\pad\proef.log"' -Wait -PassThru
$p.ExitCode
```

De eerste versie kopieert individuele bestanden. Het bundelen van bronmappen in ZIP- of tar-fragmenten volgt later. SURF geeft voor archiefbestanden een voorkeursbereik van [1 tot 200 GB](https://servicedesk.surf.nl/wiki/spaces/WIKI/pages/62227277/Data+Archive+Effective+archive+file+management).
