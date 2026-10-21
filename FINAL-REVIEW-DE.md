# Abschlussreview – Windows/.NET-10-UDP-Collector

Stand: 21. Oktober 2026. Quellstand der Implementierung: `276c3379e1751420356ca983660f401200b61e3c`.
Ursprüngliche Basis: Microsoft/SyslogDecode `5ced7c08911ea8ee9fcc31ea4b9079fb8ead41b5`.

## Umgesetzter Umfang

| Bereich | Ergebnis |
|---|---|
| .NET 10 | Alle Projekte portiert; Release-Build und Tests auf Windows erfolgreich |
| Rohdaten | Datagrammbytes im Besitz der Nachricht, Sender-IP/-Port und Empfangszeit erhalten |
| Status | Formaterkennung, Parse-Ergebnis und Diagnosen getrennt; Parsed behauptet keine vollständige Konformität |
| PRI | ASCII, Wertebereich und führende Nullen geprüft |
| RFC 5424 Header | Version, Feldlängen, ASCII, Trennzeichen und Zeitstempelgrammatik geprüft |
| Structured Data | Namen, doppelte SD-IDs, wiederholte Parameter, Escape-Regeln und vollständige Elemente geprüft |
| Encoding | Striktes UTF-8 für PARAM-VALUE und BOM-markiertes MSG; MSG-ANY behält beliebige Bytes |
| RFC 3164 | Lokale Uhrzeit/Jahresschätzung, Senderzone, DST-Diagnosen, TAG/CONTENT und Größenüberschreitung |
| Serialisierung | Feldvalidierung, Unicode/BOM, explizite Byteausgabe und UTC-Normalisierung |
| Tests | 59 MSTest-Tests und 31 Zusatzprüfungen, einschließlich kleiner UDP-Roundtrips |

## Verbleibende Grenzen

- Kein Relay, TCP oder TLS; dies gehört ausdrücklich nicht zum Projektumfang.
- Keine pauschale RFC-Zertifizierung. Registry-Eigentümerschaft privater SD-IDs und die
  Semantik sämtlicher registrierter SD-Parameter werden nicht validiert.
- RFC-3164-Jahr ist eine dokumentierte Heuristik; verspätete oder falsch datierte Logs
  bleiben mehrdeutig. Ohne Senderzone kein UTC-Zeitpunkt. Herstellerzeitstempel bleiben
  gekennzeichnete, nicht als UTC aufgelöste Erweiterungen. Historische Zeichensätze werden
  nicht geraten. Hostnamen werden syntaktisch, nicht gegen DNS/Absenderidentität geprüft.
- .NET DateTime kann Jahr 0000 und UTC-Werte außerhalb 0001–9999 nicht darstellen;
  diese werden diagnostiziert und bleiben im Originaltext erhalten.
- Serialisierung normalisiert. Für den exakten Originalinhalt gilt Source.RawData;
  Unicode-Textausgabe ist kein unveränderter Roundtrip eines beliebigen Byte-MSG.
- UDP hat keine Zustellgarantie. Empfang/Observer laufen synchron; Queue und Lebenszyklus
  wurden nicht neu entworfen. Die Queue bleibt unbegrenzt. Dauerlast mit realem Sink,
  Betrieb als Windows-Dienst, Speicherlimits/Backpressure und Verlustzähler sind nicht
  durch die kleinen CI-UDP-Tests nachgewiesen. PCAPNG/Tx.Network ist nicht praktisch geprüft.

## Relevante API-Änderungen

RFC-3164-Header.Timestamp bleibt ohne konfigurierte Senderzeitzone leer; lokale Zeit und
Jahresschätzung stehen in Rfc3164. Message enthält vollständigen CONTENT samt Trennzeichen.
Die Serialisierung weist ungültige Werte zurück und markiert Text-MSG mit BOM. Fehlendes
und leeres MSG sind verschieden. TimestampFormat ist nicht mehr veränderbar. Einzelheiten
und Konfigurationsbeispiele stehen im Änderungsprotokoll REVIEW-DE.md.

## Messverfahren

Der Workflow `windows-benchmark.yml` baut den Original- und aktuellen Bibliotheksquellstand
mit demselben net10.0-Benchmarkprojekt auf demselben Windows-Runner. Reihenfolge:
Original, aktuell, aktuell, Original; pro Prozess/Szenario/Modus 50.000 Warmup-Aufrufe
und fünf Runden zu 200.000 Nachrichten. Alle Szenarien verwenden den Standardparser mit
IP-Extraktion. Messwerte sind keine vollständige Leistungsstudie und enthalten Runnerrauschen.

`text-parse` nutzt einen vorhandenen String und ein wiederverwendetes Raw-Objekt.
`receive-and-parse` umfasst Bytekopie, Dekodierung, Raw-Objekt und Parsing, aber keinen Socket.
Der Originalpfad simuliert die Bytekopie des früheren UdpClient-Empfangs; der aktuelle Pfad
nutzt FromDatagram und behält Rohbytes. Der neue Parser leistet zusätzliche Validierung
und Metadatenarbeit. Damit ist dies ein Versionsvergleich, kein Vergleich identischer
Funktionalität. Speicherwerte stammen aus GetAllocatedBytesForCurrentThread.

Die funktionalen Tests des Implementierungsstands:
https://github.com/ATS-CE/SyslogDecode/actions/runs/39543178459

## Messergebnisse vom 21. Oktober 2026

Windows Server 2025 (Build 26100), AMD EPYC 7763, 4 logische CPUs, x64,
SDK 10.0.402, Runtime 10.0.13, Workstation-GC. Gemessen wurde Commit
`cc5d277cdf4fb0afa3c750e639b358e0fe435a6c` (Bibliothek unverändert gegenüber `276c337`).
Je Tabellenzeile: Median aus 10 Runden über zwei Prozesse pro Version.

| Modus | Szenario | Original Nachrichten/s | Aktuell Nachrichten/s | Änderung | Original Bytes/Nachricht | Aktuell Bytes/Nachricht |
|---|---|---:|---:|---:|---:|---:|
| text-parse | 3164 | 533247 | 1233094 | +131.2 % | 3264 | 2104 |
| text-parse | 5424-msg | 999444 | 1344852 | +34.6 % | 2088 | 1648 |
| text-parse | 5424-sd | 539482 | 739282 | +37.0 % | 3336 | 2656 |
| text-parse | 5424-escapes | 556137 | 764057 | +37.4 % | 3400 | 2624 |
| receive-and-parse | 3164 | 496411 | 1076200 | +116.8 % | 3472 | 2368 |
| receive-and-parse | 5424-msg | 867782 | 1195132 | +37.7 % | 2336 | 1904 |
| receive-and-parse | 5424-sd | 494606 | 657990 | +33.0 % | 3632 | 2960 |
| receive-and-parse | 5424-escapes | 511620 | 678047 | +32.5 % | 3712 | 2944 |

In den vier gemessenen Fällen sinken die Allokationen einschließlich Byteübernahme
um rund 18–32 %. Alle Szenarien sind kurze Nachrichten; große Payloads, viele Parameter,
Fehlermeldungsfluten oder abweichende Extraktoren können ein anderes Bild ergeben.
Insbesondere die frühen Textmessungen streuen deutlich (JIT-/Runner-Einflüsse): bei RFC 3164
aktuell etwa 0,59–1,28 Mio. Nachrichten/s, Original etwa 0,25–0,56 Mio. Deshalb sind die
Medianwerte Beobachtungen dieses Laufs und keine garantierte Kapazität oder statistische
Signifikanzbehauptung. RFC-3164-CONTENT/Metadaten und Validierungsumfang unterscheiden
sich absichtlich zwischen den Versionen. Es gibt keinen Anspruch auf gleiche Ausgabeobjekte.

Rohmesswerte samt Minima/Maxima: `results/windows-2026-10-21.json`.
Erfolgreicher Vergleichslauf: https://github.com/ATS-CE/SyslogDecode/actions/runs/39545499299
Die funktionalen Workflows für denselben Commit sind ebenfalls erfolgreich:
https://github.com/ATS-CE/SyslogDecode/actions/runs/39545499185
und https://github.com/ATS-CE/SyslogDecode/actions/runs/39545503757.

## Fazit und nächste Betriebsprüfung

Die fünf Korrekturschritte sind implementiert, getestet und im offenen Draft-PR dokumentiert.
Der Parser zeigt in diesem begrenzten Vergleich eine Verbesserung. Vor einer Produktions-
Kapazitätsaussage ist ein nativer Windows-Dauerlasttest mit realistischen Nachrichten,
langsamen Verbrauchern und dem vorgesehenen Speichersink erforderlich: Empfangsrate,
Verlustquote, Warteschlangenlänge, Prozessspeicher und Latenzen gemeinsam messen.
Dieser Review setzt weder einen Merge noch eine Produktivfreigabe voraus.
