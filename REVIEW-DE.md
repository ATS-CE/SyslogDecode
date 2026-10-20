# SyslogDecode: .NET 10, Durchsatz und RFC-Prüfung

Basis: microsoft/SyslogDecode, Commit `5ced7c08911ea8ee9fcc31ea4b9079fb8ead41b5`.
Prüfung: 20. Oktober 2026. Arbeitsbranch im Fork ATS-CE/SyslogDecode; der ursprüngliche Microsoft-Stand bleibt unverändert.

## Projektumfang

UDP-Collector unter Windows; TCP, TLS und Relay-Funktionen sind ausdrücklich ausgeschlossen.
Die entsprechenden Zeilen der RFC-Prüfung beschreiben den Unterschied zum vollständigen
Protokollumfang, keine offenen Implementierungsaufgaben dieses Projekts.

## Portierung und Betrieb

Bibliothek, beide Konsolenprogramme, MSTest-Projekt und Validierungsprogramm zielen auf `net10.0`.
`global.json` wählt ein stabiles .NET-10-SDK. Die bisherige Unterstützung für .NET Framework 4.7.2 und
.NET Standard 2.0 entfällt in dieser Variante. Verbraucher müssen neu für .NET 10 gebaut werden.
Keine Windows-spezifischen API-Erweiterungen nötig; der Betrieb unter Windows erfolgt nativ.
Die Lasttest-Anwendung nutzt weiterhin Tx.Network 3.0.3 und App.config, mit dem expliziten
Paket System.Configuration.ConfigurationManager 10.0.0. Ihr Main wartet jetzt auf das Ende des Sendens.
Die Paket-/Assemblyversion der Bibliothek wurde nicht als neue öffentliche Releaseversion ausgegeben.

Aus dem Repository-Hauptverzeichnis in PowerShell:

```powershell
dotnet build .\src\SyslogDecodeAll.sln -c Release
dotnet test .\src\SyslogDecode.Tests\SyslogDecode.Tests.csproj -c Release --filter FullyQualifiedName~SyslogParsingTests
dotnet run --project .\validation\Validation.csproj -c Release
dotnet run --project .\validation\Validation.csproj -c Release -- --benchmark
dotnet publish .\src\SyslogDecode.SampleApp\SyslogDecode.SampleApp.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64
```

SampleApp ist weiterhin eine Demonstrationsanwendung, kein installierter Windows-Dienst.
Der vorhandene große UDP-Lasttest ist separat auszuführen: `dotnet test .\src\SyslogDecode.Tests\SyslogDecode.Tests.csproj -c Release --filter FullyQualifiedName~SyslogLoadTest`.
Er nutzt feste Ports und behauptet verlustfreie UDP-Zustellung; das ist kein verlässliches CI-Kriterium.
Der beigefügte Windows-Workflow prüft Build, Parser und kleine lokale UDP-Übertragungen.

## Durchsatzänderungen

| Bereich | Änderung | Erwarteter Effekt |
|---|---|---|
| UDP | Ein 65536-Byte-Puffer pro Empfangsthread; Socket.ReceiveFrom statt UdpClient.Receive | Kein neues Nutzdaten-Bytearray je Datagramm; nur empfangene Länge wird dekodiert |
| PRI | ASCII-Ziffern direkt auswerten, keine Replace-Kette oder Ausnahme für gewöhnliche Fehlformate | Weniger temporäre Strings und Exceptions |
| Tokenvergleich | string.Compare mit Startindex | Kein Substring pro Match |
| RFC 3164 | Fester Zeitstempel wird direkt gescannt; Herstellerformate nutzen weiterhin Fallback | Kein Split/Substring/LINQ im üblichen Timestamp-Pfad |
| RFC 5424 | StringBuilder nur bei gültigen Escape-Sequenzen | Linearer Aufbau statt wiederholter Stringverkettung |
| Data-Dictionary | Direkt gruppieren | Keine flache Zwischenliste, GroupBy-Struktur und zusätzliche Array-Dictionary-Kopie |

Der Empfang bleibt synchron auf einem Thread. String-, Ergebnisobjekt- und Endpoint-Allokationen bleiben.
Observer laufen weiterhin synchron und können den Empfänger ausbremsen. Die Queue ist unbegrenzt;
Überlast kann weiterhin Speicherverbrauch oder UDP-Verlust verursachen. Polling/Sleep, Lebenszyklus-Rennen
und der selbst implementierte Node-Pool wurden nicht neu entworfen. Ein begrenzter Channel mit
expliziten Drop-Zählern wäre eine gesonderte, verhaltensändernde Weiterentwicklung.

## RFC-Prüfung: Ergebnis

**Keine vollständige RFC-Konformität.** Das Projekt bleibt ein toleranter Extraktionsparser.
Ein zurückgegebenes Objekt oder PayloadType.Rfc5424 ist kein Validitätsnachweis.
`ErrorMessages` muss ausgewertet und `Source.Message` für Diagnose aufbewahrt werden.
Die Tabelle ist eine statische Prüfung der zentralen Empfangs-, Parser- und Serialisierungspfade,
keine Zertifizierung und kein vollständiger Test aller normativen Anforderungen.

| Vorschrift / Bereich | Ausgangsbefund | Stand dieser Änderungen |
|---|---|---|
| Beide RFCs: PRI | Unzulässige Werte, führende Nullen und Leerzeichen konnten akzeptiert werden | ASCII, 0–191, 1–3 Ziffern und keine führenden Nullen geprüft |
| 3164 §4/4.1: 1024 Oktette | Kein Größenlimit | Offen; tolerantes Empfangen größerer Datagramme bleibt möglich |
| 3164 §4.1.2: Zeitstempel | Aufwendige Heuristik, kurze Hosts problematisch; Januarprüfung `Month == 0` unmöglich | Schneller Standardpfad und Jahreswechselkorrektur; Empfangsdatum als Jahresbasis |
| 3164 §4.1.2: lokale Zeit | Zeit ohne Zone wird als UTC interpretiert | Offen; explizite Sender-Zeitzone und Kennzeichnung geschätzter Zeit nötig |
| 3164 §4.1.3: TAG/CONTENT | ProcId-Heuristik statt sauberer TAG-Abgrenzung; Trennzeichen/Leerzeichen gehen verloren | Offen; 32-Zeichen-TAG und vollständigen CONTENT gesondert modellieren |
| 3164 §4.2/4.3: freie/fehlerhafte Nachrichten, Relay | Parser kehrt bei fehlender PRI ohne Nutztext-Auswertung zurück; keine Relay-Normalisierung | Offen; Raw-Quelle bleibt erhalten; kein vollständiges RFC-3164-Relay |
| 5424 §6.2.2: VERSION | Erkennt genau Version 1 | Version 1 unterstützt; unbekannte Versionen nicht gesondert ausgewiesen |
| 5424 §6.2.3: NIL-Zeitstempel | `-` als Fehler/MinValue statt fehlender Zeit | Behoben |
| 5424 §6.2.3: Zeitstempelsyntax | Allgemeines kulturabhängiges DateTime.TryParse statt RFC-Grammatik | Offen: Großschreibung T/Z, Pflichtzone, höchstens 6 Nachkommastellen, keine Schaltsekunde explizit prüfen |
| 5424 §6.2.4–7: Headerfelder | Generischer ReadWord trennt auch bei `[`/`]`/`=`; Grenzen 255/48/128/32 fehlen | Offen: ASCII-Felder mit exakten Längen und SP-Delimiter lesen |
| 5424 §6.3: NIL oder SD erforderlich | Fehlende SD stillschweigend akzeptiert | Fehler wird gemeldet |
| 5424 §6.3.1: eindeutige SD-ID | Zweites Element überschreibt das erste | Fehler, erstes Element bleibt erhalten |
| 5424 §6.3: SD-NAME | Namen nicht vollständig geprüft; `[` fälschlich generischer Separator | 1–32 druckbare ASCII-Zeichen, Ausschlüsse geprüft; `[` als Namenszeichen möglich |
| 5424 §6.3.3: Escapes | Unbekanntes `\q` verlor Backslash; Stringverkettung | Unbekannte Sequenzen bleiben erhalten; gültige Escapes linear dekodiert |
| 5424 §6.3.3: Parameter | Wiederholte Namen zulässig | Liste bleibt erhalten; Test ergänzt |
| 5424 §6.3: SD-Grammatik | Tolerantes Überspringen von Leerzeichen; Fehler teilweise geschluckt | Teilweise offen: genaue Abstände, transaktionale Rücknahme und SD-ID-Registrierungsregeln |
| 5424 §6.4: MSG | TrimStart entfernt Nutzdaten-Leerzeichen | Nur ein Trennzeichen entfernt, restliche Leerzeichen erhalten |
| 5424 §6.4: BOM / Bytes | Ganze Nachricht wird mit ersetzendem UTF-8-Decoder gelesen; BOM entfernt | Offen: MSG-ANY kann beliebige Bytes enthalten; Rohbytes und Encoding-Status fehlen, ungültiges UTF-8 nicht streng erkannt |
| 5424 §6.1: Größen | UDP-Pfad kann 480/2048 Oktette empfangen | Puffer bleibt ausreichend; kein transportübergreifendes Größenkonzept |
| 5424 §5.1: Transport | Nur UDP, kein TLS | Offen: kein vollständiger Syslog-Transportumfang nach RFC 5424/5425 |
| 5424 Serialisierung | NIL-SD fügt zusätzliches Leerzeichen ein | Behoben |
| 5424 Serialisierung | Datum wird unabhängig vom DateTime.Kind mit Z ausgegeben; Feldvalidierung fehlt | Offen: UTC-Normalisierung, ASCII-/Längenprüfung, UTF-8-BOM-Policy und präziser Roundtrip |

Verhaltensänderungen: Ungültige PRI wird nun abgewiesen. SD-Fehler werden häufiger gemeldet;
bei doppelter SD-ID bleibt der erste Wert statt des letzten erhalten. Führende MSG-Leerzeichen
werden bewahrt. Jahreslose BSD-Zeitstempel verwenden das Empfangsdatum. Legacy-Zeitinterpretation
bleibt ansonsten erhalten. Das ist vor Rollout mit echten Gerätenachrichten zu prüfen.

## Vergleichsbenchmark

Der Benchmark liefert fünf Messungen je Nachrichtentyp nach Warmup: Nachrichten/s und zugewiesene
Bytes/Nachricht. Er misst den Parser inklusive Standard-IP-Extraktion, nicht UDP, Queue oder Persistenz.
Er ist ein einfacher Vergleichstest, keine BenchmarkDotNet-Studie.

Für einen fairen Vorher/Nachher-Vergleich werden **beide Quellstände unter demselben .NET 10** gebaut.
Dadurch wird der Effekt der Codeänderungen von einem Runtime-Wechsel getrennt:

```powershell
git worktree add --detach ..\SyslogDecode-baseline 5ced7c08911ea8ee9fcc31ea4b9079fb8ead41b5
$baseline = (Resolve-Path ..\SyslogDecode-baseline\src\SyslogDecode).Path
dotnet run --project .\validation\Validation.csproj -c Release "-p:SyslogSource=$baseline" -- --benchmark
dotnet clean .\validation\Validation.csproj -c Release
dotnet run --project .\validation\Validation.csproj -c Release -- --benchmark
```

Das Validierungsprojekt kompiliert die gewählte Bibliotheksquelle direkt; dies ist nur für den
Vergleich erforderlich. Der normale Bibliotheksbuild läuft separat über die Solution.
Bei Verwendung des ZIPs ohne Git-Metadaten zuerst das Original-Repository separat klonen.
CPU, Windows-Version, SDK, Nachrichtengrößen, Extraktionsprofil und Empfangsverluste dokumentieren;
für den Produktionsdurchsatz zusätzlich einen externen Sender und den echten Speichersink verwenden.

## Quellen

- https://github.com/microsoft/SyslogDecode
- https://www.rfc-editor.org/rfc/rfc3164.txt
- https://www.rfc-editor.org/rfc/rfc5424.txt
- https://learn.microsoft.com/en-us/dotnet/core/compatibility/10
- https://www.nuget.org/packages/Tx.Network/3.0.3
- https://www.nuget.org/packages/System.Configuration.ConfigurationManager/10.0.0

## Ausgeführte Prüfungen

Ergebnisse werden nach Build und Testlauf unten ergänzt. Windows-spezifische Tests müssen auf Windows erfolgen.

- .NET-10-SDK 10.0.402 heruntergeladen; Runtime 10.0.13 wird vom Host gefunden.
- Der Installer meldete Fehler beim Setzen der Archiv-Besitzerrechte. Die entpackten Dateien sind vorhanden,
  aber `dotnet --info` und der Validierungslauf scheitern beim Runtime-Start mit
  `Failed to create CoreCLR, HRESULT: 0x8007000E` (Exit 137).
- Deshalb **kein erfolgreicher Compile-/Testlauf und keine gemessene Beschleunigung** in dieser Umgebung.
  Auch mit begrenztem GC-Heap und reduzierter Prozessorzahl ließ sich die Runtime nicht starten.
- Projektdateien wurden als XML geprüft, sämtliche TargetFramework-Werte auf net10.0 kontrolliert,
  obsolete direkte Framework-Referenzen entfernt und `git diff --check` ausgeführt.
- Die Tests in `validation/Program.cs` sind ausführbar vorbereitet, aber hier nicht erfolgreich ausgeführt.
  Sie decken PRI-Grenzen, NIL-Zeitstempel, MSG-Leerzeichen/BOM, SD-Escapes/Fehler/wiederholte Parameter,
  Dictionary-Zusammenführung, NIL-Serialisierung, BSD-Jahreswechsel sowie UDP-Pufferwiederverwendung ab.
- Windows-Build, Windows-UDP-Verhalten und PCAPNG-Verarbeitung mit Tx.Network bleiben praktisch zu verifizieren.
