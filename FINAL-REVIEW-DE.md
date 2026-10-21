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

Messwerte werden nach dem Windows-Lauf ergänzt. Die funktionalen Tests des Implementierungsstands:
https://github.com/ATS-CE/SyslogDecode/actions/runs/39543178459
