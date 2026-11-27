# Scanner profiles (v1.6.2 development)

Legacy config with empty ScannerProfiles retains original single TCP behavior. In GUI > 設備設定 > 多掃描器 / 串列, add profiles or choose 上下掃範本. A non-empty list replaces legacy scanner settings; save settings before starting. Use distinct device IDs. Example SC2-UPPER / SC2-LOWER; these are physical positions, not PCB T/B sides. All raw events share one SQLite outbox and existing MQTT/HTTP wire schema. No BoardPass pairing or material deduction.

- tcp-connect: scanner is TCP server; set its IP and port (often 55256).
- tcp-listen: scanner is TCP client; set local bind IP, unique local port per reader and expected peer IP. Shared listener port/master-slave combined payloads need a documented sender-identity mapping and are not supported by this implementation.
- serial: Windows COM port for RS232 or RS485 adapter in transparent byte mode. Set baud, parity, data bits, stop bits, flow control, framing. No PLC fieldbus protocol or RS485 address polling.
- Framing LF/CR/CRLF/idle; prefer explicit delimiter. Passive read supports scanner configured continuous/external trigger output. No automatic trigger commands, configuration writes, or firmware control.
- Per-device dedup uses (device_id, barcode); same barcode from upper and lower is preserved. Never infer PCB count from scan count.
- GUI simulator is single-device only; offline preparation clears profile list in the form and chooses a new SQLite path. For two readers use separate CLI simulator processes on loopback ports, not actual production readers.
- TCP connect/listen and independent identities covered by CLI self-test. Physical serial port, RS485 electrical configuration and HF811 actual trigger behavior must be tested onsite. Profinet, EtherNet/IP and Modbus TCP require documented device/PLC data map and are not implemented.

## v1.6.3
Device monitor opens from overview and reads lifetime raw-scan counters per device from the local SQLite database. Multi-profile connection states are updated directly by the transport. Offline preparation offers single or dual local simulator. MQTT SyncAgent reuses its session for nonempty batches, reconnects/resubscribes on failure; SQLite remains authoritative. This is not a durable MQTT broker session (clean session is intentional).

## Manual software trigger
TCP profiles may configure TriggerOnHex and TriggerOffHex (1–128 exact bytes, including the configured suffix if required). Blank disables the action. Use the monitor's explicit ON/OFF commands only on an approved test device. No default hardware command, no automated trigger or retry; a send does not prove a scan. Honeywell support article 000097831 describes a customized trigger string, with TRIGGER as an example, not a universal device setting. This adapter assumes the configured data TCP socket also accepts trigger commands; dedicated control sockets and serial trigger require separate verification.

## Group and PLC diagnostics
HF81x user guide chapter 12 documents network grouping with parent/child and aggregated output. No universal field format is assumed. `group-preview --separator 7C --members UP,DOWN --frame 504342317C4E4F52454144` is a synthetic example, not an HF811 default. It preserves the source frame and rejects ambiguous counts; no derived scan events are persisted. Verify actual DataMax Plus configuration and captures before adding runtime decoding.
`plc-read --host <approved-test-ip> --unit <documented-id> --function 3 --address <zero-based-documented-offset> --count <documented-count>` is a one-shot read-only Modbus TCP diagnostic (FC03/FC04). It does not infer barcode or cycle IDs, write registers, or auto-discover devices. Real HF811 register/handshake map, EDS/assembly layout (EtherNet/IP), GSDML/I/O layout (PROFINET), and PLC ownership must be verified before production adapters are enabled. Use vendor-approved isolated test hardware only.
