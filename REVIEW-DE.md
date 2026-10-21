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


## Schritt 1: Rohdatenerhalt und Verarbeitungsstatus

`RawSyslogMessage.FromDatagram` übernimmt eine eigene Kopie der Originalbytes in `RawData`.
Der UDP-Listener setzt zusätzlich `HasRawData`, `ReceivedOn`, `SourceIpAddress` und `SourcePort`.
Ein leeres Datagramm hat `HasRawData=true` und null Bytes. Bei alten, ausschließlich als String
übergebenen Nachrichten bleibt `HasRawData=false`; Originalbytes werden nicht durch erneutes
Kodieren vorgetäuscht. `Message` bleibt die bisherige UTF-8-Ansicht; bei ungültigen Bytefolgen
ist ausschließlich `RawData` verlustfrei. Vollständige feldbezogene Encodingprüfung folgt in Schritt 3.

Die Rohdatenkopie benötigt pro Datagramm ein eigenes Bytearray. Die vorherige Optimierung
"kein Bytearray je Nachricht" gilt damit nicht mehr; der Socket-Puffer selbst wird weiterverwendet.
Diese Kosten sind für die unbegrenzt von Aufrufern aufbewahrbaren Nachrichten bewusst akzeptiert.
Die Speicherlebensdauer wird nicht durch Pool-Rückgaben oder Dispose-Pflichten eingeschränkt.

| Eigenschaft | Bedeutung |
|---|---|
| `PayloadType` | Erkanntes Format, ausdrücklich kein Konformitätsnachweis |
| `HasPriority` | Facility/Severity wurden aus einer gültigen PRI gelesen; sonst sind die Enum-Standardwerte bedeutungslos |
| `ParseStatus.NotParsed` | Ergebnis wurde noch nicht durch den zentralen Parser verarbeitet |
| `ParseStatus.Parsed` | Parser hat Felder ohne gemeldete Fehler extrahiert |
| `ParseStatus.ParsedWithErrors` | Parser hat ein Ergebnis geliefert und dabei Fehler gemeldet |
| `ParseStatus.Unrecognized` | Kein strukturiertes Format erkannt bzw. keine gültige PRI; Rohdaten bleiben erhalten |
| `ParseStatus.Failed` | Verarbeitung abgebrochen, etwa bei leerem Input oder Parser-/Extractor-Ausnahme; Teildaten können enthalten sein |
| `ValidationStatus.NotValidated` | Keine vollständige RFC-Validierung durchgeführt; auch bei Parsed |
| `ValidationStatus.ErrorsDetected` | Mindestens ein Verarbeitungs-/Validierungsproblem bekannt |
| `Diagnostics` | Fehlercode, Verarbeitungsphase, Meldung, optional UTF-16-Position in der Parser-Textansicht |

Es gibt bewusst keinen Status "RFC-konform", solange die vollständige Prüfung noch fehlt.
`ErrorMessages` bleibt aus Kompatibilitätsgründen vorhanden; neue Parser sollten `ctx.AddError`
verwenden. Direkte Ergänzungen der Legacy-Liste werden übernommen; Löschen/Umsortieren dieser
Liste während des Parsens gehört nicht zum unterstützten Erweiterungsvertrag.

Ein erkanntes fehlerhaftes Format wird nicht nachträglich als fehlerfreier Plaintext umgedeutet.
Version-1-Nachrichten werden für den RFC-5424-Parser reserviert. Ein Variant-Parser muss nach
Erkennen seines Formats `PayloadType` setzen; `false` ohne Format/Fehler bedeutet "kein Treffer",
und dabei vorläufig geschriebene Felder werden verworfen. Bereits erkannte fehlerhafte Nachrichten
behalten ihre Teildaten und Fehler. Benutzerdefinierte Parser mit eigener Version-1-Syntax müssen
von `Rfc5424SyslogParser` ableiten oder die zentrale Erkennung gesondert anpassen.

Parser- und Extractor-Ausnahmen werden als Failed-Ergebnis ausgegeben, sodass der Stream die
betroffene Nachricht weiterreichen kann. Ressourcen-/Prozessfehler wie OutOfMemoryException
werden nicht in normale Nachrichtenfehler umgewandelt. Dies ist keine Zustellgarantie für UDP
oder für fehlerhafte nachgelagerte Observer.

Neue Tests: Originalbytes einschließlich BOM/ungültigem UTF-8/NUL, Datenbesitz nach Pufferänderung,
Senderdaten, leere Datagramme, Textkompatibilität, Fehlerstatus, keine Formatumdeutung,
keine spekulativen Feldreste, Legacy-Diagnosen und Stream-Ausgabe nach Parser-Ausnahmen.

## Schritt 2 – RFC-5424-Textgrammatik

Headerfelder werden auf PRINTUSASCII, maximale Feldlänge und genau einen SP als
Trennzeichen geprüft. Numerische Versionskandidaten werden nicht als anderes Format
umgedeutet; nur Version 1 wird unterstützt. VERSION und der originale TIMESTAMP-Text
stehen zusätzlich im Header. Diagnosen enthalten jetzt optional den Feldnamen.

Zeitstempel werden einschließlich Kalenderdatum, Großschreibung von T/Z, Sekunden
0..59, 1..6 Nachkommastellen und numerischem Offset geprüft. UTC-Umrechnung erfolgt
auch für syntaktisch zulässige Offsets außerhalb von DateTimeOffsets 14-Stunden-Grenze.
Jahr 0000 und UTC-Werte außerhalb des DateTime-Bereichs erzeugen TimestampOutOfRange;
der Originaltext bleibt erhalten. -00:00 bleibt im Originaltext unterscheidbar.
Ungültige Zeitstempel erlauben die weitere Extraktion (ParsedWithErrors), fehlerhafte
Feldgrenzen oder Structured Data brechen mit Failed ab.

Structured Data prüft Namen, Trennzeichen, Anführungszeichen, Escape-Regeln und doppelte
SD-IDs. Unbekannte Escapes bleiben unverändert, wiederholte Parameternamen bleiben
zulässig. Nur vollständige Elemente werden übernommen; bereits vollständige Elemente
bleiben bei einem späteren Fehler erhalten. Private SD-IDs werden auf eine dezimale
Enterprise-Suffixsyntax einschließlich punktgetrennter Unterkennungen geprüft.
Registry-Zugehörigkeit und Eigentümerschaft werden nicht behauptet oder online geprüft.

15 zusätzliche Testmethoden decken Headergrenzen, Trunkierung, Zeitstempel, SD-Namen,
Escapes, Duplikate und diagnostizierte Positionen ab. Die CI führt sie zusätzlich zu
den bisherigen Tests aus. Bytebezogene UTF-8-/BOM-Prüfung folgt in Schritt 3;
Parsed ist weiterhin kein vollständiger RFC-Konformitätsnachweis.

Normative Grundlage: https://www.rfc-editor.org/rfc/rfc5424.html, Abschnitte 6,
6.2, 6.3 und 7.2.2. Frühere Tabellen dieses Dokuments beschreiben den ursprünglichen
Reviewstand; die oben genannten Grammatikabweichungen sind mit Schritt 2 korrigiert.

## Schritt 3 – Bytegenaue UTF-8- und BOM-Prüfung

RFC-5424-PARAM-VALUE wird bei Datagrammen anhand der Originalbytes auf gültiges,
kürzestes UTF-8 geprüft. Fehlerhafte Werte werden nicht als Ersatzzeichen interpretiert;
das betroffene SD-Element wird nicht übernommen. Vollständige vorherige Elemente bleiben.
Die Prüfung akzeptiert Unicode-Skalare einschließlich Steuerzeichen und U+FFFD.

MSG wird anhand seines tatsächlichen Anfangs unterschieden: EF BB BF kennzeichnet
UTF-8, das streng geprüft wird. Bei Fehlern bleibt Message null, während MessageBytes
und Source.RawData erhalten bleiben. Ohne BOM ist MSG-ANY zulässig; Message ist hier
nur die bisherige UTF-8-Anzeigeansicht, keine Aussage über die tatsächliche Kodierung.
MessageEncoding kennzeichnet dies als Unspecified. Ein weiterer BOM innerhalb des MSG
ist Nutzinhalt. Ein BOM vor PRI wird bei Datagrammen nicht mehr stillschweigend entfernt.

MessageBytes ist eine Ansicht auf die bereits besessenen Datagrammbytes einschließlich
BOM, ohne zusätzliche Kopie. HasMessageBytes unterscheidet ein vorhandenes leeres MSG
von fehlendem MSG bzw. Stringeingaben. BytePosition in Diagnosen bezeichnet den absoluten
Byteoffset im Datagramm; Position bleibt ein separater optionaler UTF-16-Index.
Bei Stringeingaben werden unpaarige Surrogate diagnostiziert, aber keine ursprüngliche
Bytekodierung behauptet (TextOnly). Die bisherige BOM-Textkompatibilität bleibt erhalten.
Die Parseransicht von Datagrammen stammt aus den Originalbytes und bleibt unabhängig von
nachträglichen Änderungen am öffentlichen Message-Anzeigefeld.

Neun Testmethoden prüfen gültiges Unicode, Überlängen, Surrogate, ungültige Startbytes,
abgeschnittene Sequenzen, BOM-Position, MSG-ANY, Byteoffsets und Textkompatibilität.
Grundlage: RFC 5424 Abschnitte 6, 6.3.3 und 6.4 sowie RFC 3629. Die übrigen
RFC-3164-/Collector-Prüfungen bleiben separat; Parsed ist kein Konformitätszertifikat.

## Schritt 4 – RFC-3164-Collector

RFC-3164-Zeit wird nicht mehr stillschweigend als UTC ausgegeben. Rfc3164.LocalTimestamp
enthält die lokale Uhrzeit mit Kind=Unspecified, TimestampText das Original. Das Jahr
wird als nächstliegendes gültiges Datum im Empfangsjahr oder einem seiner Nachbarjahre
ermittelt und durch YearInferred kenntlich gemacht. Diese Heuristik kann verspätete oder
falsch datierte Geräteereignisse nicht eindeutig auflösen. ReceivedOn soll UTC sein;
Kind=Unspecified wird für diesen Empfangszeitpunkt ausdrücklich als UTC behandelt.
Fehlt ReceivedOn, bleibt das Jahr unbekannt und eine Diagnose wird ausgegeben.

Ohne Senderzeitzone bleibt Header.Timestamp null (TimeStatus=ZoneUnknown). Eine Zone
kann pro Quelle über Rfc3164SyslogParser.SenderTimeZoneResolver konfiguriert werden:

```csharp
var parser = SyslogMessageParser.CreateDefault();
var bsd = parser.VariantParsers.OfType<Rfc3164SyslogParser>().Single();
bsd.SenderTimeZoneResolver = raw => TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
```

In Produktion die Zone anhand der Senderkonfiguration wählen; nicht aus dem Hostnamen
erraten. Die Zone sollte außerhalb des Callbacks aufgelöst und wiederverwendet werden.
Nur eine eindeutige lokale Zeit ergibt EstimatedUtc. DST-Lücken und doppelte Uhrzeiten
werden diagnostiziert; Header.Timestamp bleibt null. Herstellerzeitformate bleiben
optional über AllowVendorTimestamps lesbar, werden jedoch als VendorTimestamp markiert
und nicht mehr durch Legacy-Heuristiken als vertrauenswürdige UTC-Werte ausgegeben.

TAG wird als ASCII-alphanumerische Folge gelesen; mehr als 32 Zeichen erzeugen eine
Diagnose ohne Abschneiden. Rfc3164.Content und Message enthalten den vollständigen
CONTENT ab dem ersten Nicht-TAG-Zeichen, einschließlich ':', '[PID]' und Leerzeichen.
Das ist eine bewusste Änderung gegenüber dem alten Trim-/ProcId-Verhalten. AppName ist
TAG; eine führende numerische [PID] wird zusätzlich als ProcId extrahiert. Bei Datagrammen
enthält MessageBytes den gesamten ursprünglichen MSG-Teil (TAG + CONTENT).

Ein Datagramm über 1024 Oktette erhält MessageTooLong, wird aber nicht gekürzt oder
verworfen. Stringeingaben haben keine behauptete ursprüngliche Byteanzahl. Ungültige
Header, Zeitkomponenten und ASCII-Steuerzeichen im MSG werden diagnostiziert; Rohdaten
bleiben erhalten. Freie Nachrichten ohne PRI bleiben Unrecognized mit Originalinhalt;
es werden keine Relay-Header hinzugefügt. Hostnamen werden syntaktisch geprüft, nicht
gegen DNS/Senderidentität verifiziert. Beliebige historische MSG-Zeichensätze werden
weiterhin nicht geraten; Message ist deren Anzeigeansicht, RawData die Originalquelle.

Elf zusätzliche Testmethoden prüfen Zeit-/DST-Grenzen, TAG/CONTENT, Bytegrößen, Hostnamen
und Rohdatenerhalt. Normative Grundlage: RFC 3164 Abschnitte 4.1–4.2. Serialisierung und
abschließende Durchsatzmessung bleiben gesonderte Aufgaben.

## Schritt 5 – RFC-5424-Serialisierung

Serialize und SerializeBytes erzeugen validierte Version-1-Ausgabe. Facility/Severity,
Headerlängen/ASCII, SD-Namen, Parameter und Unicode-Skalare werden vor Ausgabe geprüft.
Ungültige Werte führen zu ArgumentException; fehlende Headerwerte werden ausschließlich
bei null zu NILVALUE. Leere Strings sind keine gültigen Headerfelder. Leere Parameterwerte
müssen als leerer String statt null angegeben werden. RFC-3164-Ergebnisse und fehlerhafte
Parsergebnisse werden nicht stillschweigend umgeschrieben; dafür ist ein explizit neu
aufgebauter, korrigierter RFC-5424-Eintrag erforderlich.

UTC-Zeit wird kulturunabhängig ausgegeben. Local wird über die lokale Systemzeitzone
normalisiert; DST-Lücken, mehrdeutige lokale Zeiten, Bereichsüberläufe und Unspecified
werden zurückgewiesen. Maximal sechs Nachkommastellen sind erlaubt: die letzte
100-ns-Stelle wird abgeschnitten, nicht aufgerundet. Ein unveränderter gültiger
TimestampText wird beibehalten, einschließlich Offset und -00:00. Nach Änderung des
Timestamp-Werts wird neu normalisiert. TimestampFormat ist jetzt eine feste Konstante;
prozessweite benutzerdefinierte Ausgabeformate werden nicht mehr unterstützt.

Text-MSG erhält immer einen UTF-8-BOM. null bedeutet fehlendes MSG, ein leerer String
bedeutet vorhandenes leeres Unicode-MSG. Ein führendes U+FEFF im Nutztext bleibt zusätzlich
zum Kodierungsmarker erhalten. SerializeBytes(entry, messageBytes) wählt ausdrücklich
einen Byte-MSG: ohne BOM sind beliebige Bytes erlaubt, mit BOM ist striktes UTF-8 nötig.
Diese Überladung ignoriert Message zugunsten des expliziten Bytearguments. Die normale
Überladung nutzt dagegen immer das aktuelle Message-Feld, niemals veraltete MessageBytes.
Ein exakter Roundtrip des vollständigen Originaldatagramms erfolgt über Source.RawData;
Serialisierung normalisiert und validiert bewusst. Die Empfängerfunktion bleibt ohne Relay.

SyslogUdpSender.Send(entry) nutzt die Byte-Serialisierung. Send(string) bleibt die
unvalidierte Low-Level-Ausgabe bereits fertig aufgebauter Textnachrichten. Zwölf neue
Testmethoden prüfen Grenzwerte, Unicode, Zeit/Kultur, SD-Escapes, Byte-Roundtrips und die
tatsächliche UDP-Ausgabe. Grundlage: RFC 5424 Abschnitte 6.2–6.4 und RFC 3629.
