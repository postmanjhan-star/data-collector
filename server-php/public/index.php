<?php
declare(strict_types=1);

/**
 * HF811 v1.5 PHP 8.1+ JSON receiver, compatible with the C# Collector.
 * Set the web document root to server-php/public, enforce HTTPS at the proxy.
 * Store raw scan events ONLY: this is not an MES pass judgment.
 */

header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store');
header('X-Content-Type-Options: nosniff');

function reply(int $code, array $payload): never {
    http_response_code($code);
    echo json_encode($payload, JSON_UNESCAPED_UNICODE | JSON_THROW_ON_ERROR);
    exit;
}

function authorized(): bool {
    $key = getenv('HF811_SERVER_API_KEY') ?: '';
    if (strlen($key) < 16) {
        error_log('HF811 receiver: HF811_SERVER_API_KEY is not configured');
        reply(503, ['error' => 'Server not configured']);
    }
    $presented = $_SERVER['HTTP_X_COLLECTOR_KEY'] ?? '';
    // Compare same-length digests in constant time, including missing headers.
    return $presented !== '' &&
        hash_equals(hash('sha256', $key, true), hash('sha256', $presented, true));
}

function connect_db(): PDO {
    $dsn = getenv('HF811_PDO_DSN') ?: 'sqlite:' . dirname(__DIR__) . '/data/receiver.sqlite3';
    if (str_starts_with($dsn, 'sqlite:') && !in_array(substr($dsn, 7), [':memory:', ''], true)) {
        $folder = dirname(substr($dsn, 7));
        if (!is_dir($folder) && !mkdir($folder, 0770, true) && !is_dir($folder)) {
            throw new RuntimeException('Cannot initialize receiver data directory');
        }
    }
    $db = new PDO($dsn, getenv('HF811_DB_USER') ?: null, getenv('HF811_DB_PASSWORD') ?: null, [
        PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
        PDO::ATTR_EMULATE_PREPARES => false,
        PDO::ATTR_DEFAULT_FETCH_MODE => PDO::FETCH_ASSOC,
    ]);
    if ($db->getAttribute(PDO::ATTR_DRIVER_NAME) === 'sqlite') {
        $db->exec('PRAGMA journal_mode=WAL');
        $db->exec('PRAGMA busy_timeout=5000');
        $db->exec(<<<'SQL'
            CREATE TABLE IF NOT EXISTS scan_events(
              event_id TEXT PRIMARY KEY,
              collector_id TEXT NOT NULL,
              device_id TEXT NOT NULL,
              barcode TEXT NOT NULL,
              received_at_utc TEXT NOT NULL,
              raw_hex TEXT NOT NULL,
              source_address TEXT NOT NULL,
              is_duplicate INTEGER NOT NULL,
              is_no_read INTEGER NOT NULL,
              ingested_at_utc TEXT NOT NULL
            )
        SQL);
        $db->exec('CREATE INDEX IF NOT EXISTS idx_php_receiver_collector ON scan_events(collector_id, received_at_utc)');
    } elseif ($db->getAttribute(PDO::ATTR_DRIVER_NAME) === 'mysql') {
        $db->exec(<<<'SQL'
            CREATE TABLE IF NOT EXISTS scan_events(
              event_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
              collector_id VARCHAR(96) NOT NULL,
              device_id VARCHAR(128) NOT NULL,
              barcode TEXT NOT NULL,
              received_at_utc VARCHAR(64) NOT NULL,
              raw_hex TEXT NOT NULL,
              source_address VARCHAR(256) NOT NULL,
              is_duplicate TINYINT(1) NOT NULL,
              is_no_read TINYINT(1) NOT NULL,
              ingested_at_utc VARCHAR(40) NOT NULL,
              INDEX idx_php_receiver_collector (collector_id, received_at_utc)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
        SQL);
    } else {
        throw new RuntimeException('Only PDO MySQL and SQLite are supported');
    }
    return $db;
}

function validate_event(array $e): ?string {
    foreach (['event_id','device_id','barcode','received_at_utc','raw_hex','source_address'] as $k) {
        if (!array_key_exists($k, $e) || !is_string($e[$k])) return 'Invalid ' . $k;
    }
    foreach (['is_duplicate','is_no_read'] as $k) {
        if (!array_key_exists($k, $e) || !is_bool($e[$k])) return 'Invalid ' . $k;
    }
    if (trim($e['device_id']) === '' || strlen($e['device_id']) > 128) return 'Invalid device_id';
    if (strlen($e['barcode']) > 4096 * 4) return 'Invalid barcode length';
    if (strlen($e['received_at_utc']) > 64) return 'Invalid received_at_utc';
    try {
        new DateTimeImmutable($e['received_at_utc']);
    } catch (Exception) {
        return 'Invalid received_at_utc';
    }
    if (strlen($e['raw_hex']) > 8192 || !preg_match('/^(?:[0-9a-fA-F]{2})*$/D', $e['raw_hex']))
        return 'Invalid raw_hex';
    if (strlen($e['source_address']) > 256) return 'Invalid source_address';
    return null;
}

function same_event(array $stored, string $collector, array $e): bool {
    foreach (['device_id','barcode','received_at_utc','raw_hex','source_address'] as $k) {
        if ($stored[$k] !== $e[$k]) return false;
    }
    return $stored['collector_id'] === $collector
        && (bool)$stored['is_duplicate'] === $e['is_duplicate']
        && (bool)$stored['is_no_read'] === $e['is_no_read'];
}

function get_stored(PDO $db, string $id): ?array {
    $query = $db->prepare('SELECT * FROM scan_events WHERE event_id = ?');
    $query->execute([$id]);
    $row = $query->fetch();
    return $row ?: null;
}

function ingest(PDO $db, string $collector, array $e): bool {
    // One transaction per event: committed BEFORE ACK. Retrying a failed batch is safe.
    $db->beginTransaction();
    try {
        $stored = get_stored($db, $e['event_id']);
        if ($stored !== null) {
            $same = same_event($stored, $collector, $e);
            $db->commit();
            return $same;
        }
        $insert = $db->prepare(<<<'SQL'
            INSERT INTO scan_events
              (event_id,collector_id,device_id,barcode,received_at_utc,raw_hex,
               source_address,is_duplicate,is_no_read,ingested_at_utc)
            VALUES (?,?,?,?,?,?,?,?,?,?)
        SQL);
        $insert->execute([
            $e['event_id'], $collector, $e['device_id'], $e['barcode'],
            $e['received_at_utc'], $e['raw_hex'], $e['source_address'],
            (int)$e['is_duplicate'], (int)$e['is_no_read'],
            gmdate('Y-m-d\TH:i:s\Z'),
        ]);
        $db->commit();
        return true;
    } catch (PDOException $ex) {
        if ($db->inTransaction()) $db->rollBack();
        // Concurrent insert: compare winner instead of blindly acknowledging.
        if (in_array($ex->getCode(), ['23000', '19'], true)) {
            $winner = get_stored($db, $e['event_id']);
            if ($winner !== null) return same_event($winner, $collector, $e);
        }
        throw $ex;
    } catch (Throwable $ex) {
        if ($db->inTransaction()) $db->rollBack();
        throw $ex;
    }
}

$path = parse_url($_SERVER['REQUEST_URI'] ?? '/', PHP_URL_PATH) ?: '/';
$method = $_SERVER['REQUEST_METHOD'] ?? 'GET';
if ($method === 'GET' && $path === '/health') {
    reply(200, ['status' => 'ok', 'role' => 'HF811Receiver']);
}
if (!in_array([$method, $path], [
    ['GET', '/api/v1/collector/stats'],
    ['POST', '/api/v1/collector/scans/batch'],
], true)) {
    reply(404, ['error' => 'Not found']);
}
if (!authorized()) reply(401, ['error' => 'Unauthorized']);

try {
    $db = connect_db();
    if ($method === 'GET') {
        $stats = $db->query('SELECT COUNT(*) AS total, COUNT(DISTINCT collector_id) AS collectors FROM scan_events')->fetch();
        reply(200, ['total' => (int)$stats['total'], 'collectors' => (int)$stats['collectors']]);
    }

    $body = file_get_contents('php://input', false, null, 0, 2 * 1024 * 1024 + 1);
    if ($body === false || strlen($body) > 2 * 1024 * 1024) reply(413, ['error' => 'Payload too large']);
    try { $payload = json_decode($body, true, 64, JSON_THROW_ON_ERROR); }
    catch (JsonException) { reply(400, ['error' => 'Invalid JSON']); }
    $collector = is_array($payload) ? ($payload['collector_id'] ?? null) : null;
    $events = is_array($payload) ? ($payload['events'] ?? null) : null;
    if (!is_string($collector) || !preg_match('/^[A-Za-z0-9_.-]{1,96}$/D', $collector) ||
        !is_array($events) || !array_is_list($events) || count($events) < 1 || count($events) > 100) {
        reply(400, ['error' => 'Invalid collector_id or event list (limit 100)']);
    }
    $ids = [];
    foreach ($events as $e) {
        if (!is_array($e) || !is_string($e['event_id'] ?? null)
            || !preg_match('/^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/D', $e['event_id'])
            || isset($ids[$e['event_id']])) {
            reply(400, ['error' => 'Invalid or repeated event_id in batch']);
        }
        $ids[$e['event_id']] = true;
    }

    $acknowledged = [];
    $rejected = [];
    foreach ($events as $e) {
        $reason = validate_event($e);
        if ($reason !== null) {
            $rejected[] = ['event_id' => $e['event_id'], 'reason' => $reason];
            continue;
        }
        if (ingest($db, $collector, $e)) {
            $acknowledged[] = $e['event_id'];
        } else {
            $rejected[] = ['event_id' => $e['event_id'],
                'reason' => 'event_id already exists with different content'];
        }
    }
    reply(200, ['acknowledged_event_ids' => $acknowledged, 'rejected' => $rejected]);
} catch (Throwable $ex) {
    error_log('HF811 receiver unexpected failure: ' . get_class($ex));
    reply(500, ['error' => 'Internal server error; safely retry with same event_id']);
}
