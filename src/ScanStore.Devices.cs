namespace HF811Collector;

public sealed record DeviceSnapshot(string DeviceId, string State, long Total, long Accepted, long Duplicates, long NoReads, string? LastScanUtc);

public sealed partial class ScanStore
{
    private readonly Dictionary<string, string> _deviceStates = new(StringComparer.Ordinal);
    public void SetDeviceState(string id, string state) { lock (_gate) _deviceStates[id] = state; }

    public IReadOnlyList<DeviceSnapshot> DeviceSnapshots()
    {
        lock (_gate)
        {
            var result = _deviceStates.ToDictionary(k => k.Key, k => new DeviceSnapshot(k.Key, k.Value, 0, 0, 0, 0, null), StringComparer.Ordinal);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT device_id, COUNT(*), SUM(CASE WHEN is_duplicate=0 AND is_no_read=0 THEN 1 ELSE 0 END),
                       SUM(is_duplicate), SUM(is_no_read), MAX(received_at_utc)
                FROM scans GROUP BY device_id
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                result[id] = new DeviceSnapshot(id, _deviceStates.GetValueOrDefault(id, "inactive"), reader.GetInt64(1),
                    reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetString(5));
            }
            return result.Values.OrderBy(v => v.DeviceId).ToList();
        }
    }
}
