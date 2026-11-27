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
