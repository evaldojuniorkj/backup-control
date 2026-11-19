using Npgsql;
using System.Text.Json;

public sealed class Database(NpgsqlDataSource source)
{
    public async Task<List<Dictionary<string, object?>>> Query(string sql, params object?[] args)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var arg in args) command.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var i=0; i<reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetDataTypeName(i)=="jsonb" ? JsonSerializer.Deserialize<JsonElement>(reader.GetString(i)) : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
    public async Task Execute(string sql, params object?[] args) => await Query(sql, args);
    public async Task Initialize() => await Execute("""
        CREATE TABLE IF NOT EXISTS clients (
          id uuid PRIMARY KEY, name text NOT NULL, slug text UNIQUE NOT NULL, database text NOT NULL,
          machine_name text NOT NULL DEFAULT '', interval_hours int NOT NULL DEFAULT 24,
          grace_minutes int NOT NULL DEFAULT 60, enabled boolean NOT NULL DEFAULT true,
          token_hash text NOT NULL, last_seen timestamptz, created_at timestamptz NOT NULL DEFAULT now());
        CREATE TABLE IF NOT EXISTS commands (
          id uuid PRIMARY KEY, client_id uuid NOT NULL REFERENCES clients(id) ON DELETE CASCADE,
          backup_type text NOT NULL, state text NOT NULL DEFAULT 'Pending', created_at timestamptz NOT NULL DEFAULT now(),
          claimed_at timestamptz, finished_at timestamptz);
        CREATE UNIQUE INDEX IF NOT EXISTS one_active_command ON commands(client_id) WHERE state IN ('Pending','Claimed');
        CREATE TABLE IF NOT EXISTS runs (
          id uuid PRIMARY KEY, client_id uuid NOT NULL REFERENCES clients(id) ON DELETE CASCADE,
          event_id uuid NOT NULL, command_id uuid REFERENCES commands(id), backup_type text NOT NULL,
          status text NOT NULL, started_at timestamptz NOT NULL, finished_at timestamptz NOT NULL,
          size_bytes bigint NOT NULL, file_name text NOT NULL, message text NOT NULL,
          received_at timestamptz NOT NULL DEFAULT now(), UNIQUE(client_id,event_id));
        CREATE UNIQUE INDEX IF NOT EXISTS one_result_per_command ON runs(command_id) WHERE command_id IS NOT NULL;
        CREATE TABLE IF NOT EXISTS settings (id int PRIMARY KEY CHECK(id=1), payload text NOT NULL);
        CREATE TABLE IF NOT EXISTS installation_settings(id int PRIMARY KEY CHECK(id=1),payload text NOT NULL);
        CREATE TABLE IF NOT EXISTS installer_enrollments(id uuid PRIMARY KEY,client_id uuid NOT NULL REFERENCES clients(id) ON DELETE CASCADE,key_hash text UNIQUE NOT NULL,protected_key text NOT NULL,central_url text NOT NULL,expires_at timestamptz NOT NULL,revoked boolean NOT NULL DEFAULT false,protected_reply text,redeemed_instance_id uuid,redeemed_token_hash text,redeemed_at timestamptz);
        CREATE TABLE IF NOT EXISTS machine_registrations(
          id uuid PRIMARY KEY,instance_id uuid NOT NULL,secret_hash text NOT NULL,code text UNIQUE NOT NULL,
          machine_name text NOT NULL,requested_name text NOT NULL,existing_token_hash text,central_url text NOT NULL,
          status text NOT NULL DEFAULT 'Pending',created_at timestamptz NOT NULL DEFAULT now(),expires_at timestamptz NOT NULL,
          client_id uuid REFERENCES clients(id) ON DELETE SET NULL,enrollment_id uuid REFERENCES installer_enrollments(id) ON DELETE SET NULL,decided_at timestamptz);
        CREATE INDEX IF NOT EXISTS machine_registrations_created ON machine_registrations(created_at DESC);
        CREATE TABLE IF NOT EXISTS audit (id bigserial PRIMARY KEY, action text NOT NULL, detail text NOT NULL, created_at timestamptz NOT NULL DEFAULT now());
        ALTER TABLE clients ADD COLUMN IF NOT EXISTS agent_instance_id uuid;
        ALTER TABLE clients ADD COLUMN IF NOT EXISTS differential_interval_hours int NOT NULL DEFAULT 0;
        ALTER TABLE clients ADD COLUMN IF NOT EXISTS agent_telemetry jsonb;
        ALTER TABLE clients ADD COLUMN IF NOT EXISTS telemetry_received_at timestamptz;
        ALTER TABLE clients ADD COLUMN IF NOT EXISTS agent_version text;
        ALTER TABLE clients ADD COLUMN IF NOT EXISTS clock_skew_seconds double precision;
        ALTER TABLE clients ADD COLUMN IF NOT EXISTS clock_sampled_at timestamptz;
        ALTER TABLE clients ADD COLUMN IF NOT EXISTS clock_agent_time timestamptz;
        ALTER TABLE clients ADD COLUMN IF NOT EXISTS last_result_error text;
        ALTER TABLE clients ADD COLUMN IF NOT EXISTS last_result_rejected_at timestamptz;
        ALTER TABLE clients ADD COLUMN IF NOT EXISTS pending_results int;
        ALTER TABLE installation_settings ADD COLUMN IF NOT EXISTS sql_revision uuid NOT NULL DEFAULT gen_random_uuid();
        ALTER TABLE runs ADD COLUMN IF NOT EXISTS reported_started_at timestamptz;
        ALTER TABLE runs ADD COLUMN IF NOT EXISTS reported_finished_at timestamptz;
        ALTER TABLE runs ADD COLUMN IF NOT EXISTS time_quality text NOT NULL DEFAULT 'AgentClock';
        ALTER TABLE runs ADD COLUMN IF NOT EXISTS clock_adjustment_seconds double precision;
        ALTER TABLE commands ADD COLUMN IF NOT EXISTS claimed_by uuid;
        ALTER TABLE clients ADD COLUMN IF NOT EXISTS remote_update_supported boolean NOT NULL DEFAULT false;
        ALTER TABLE clients ADD COLUMN IF NOT EXISTS deletion_pending boolean NOT NULL DEFAULT false;
        ALTER TABLE clients ADD COLUMN IF NOT EXISTS remote_deactivation_supported boolean NOT NULL DEFAULT false;
        ALTER TABLE clients ADD COLUMN IF NOT EXISTS deactivation_id uuid;
        ALTER TABLE clients ADD COLUMN IF NOT EXISTS deactivation_requested_at timestamptz;
        ALTER TABLE clients ADD COLUMN IF NOT EXISTS deactivation_confirmed_at timestamptz;
        UPDATE clients SET enabled=false,deactivation_id=COALESCE(deactivation_id,gen_random_uuid()),deactivation_requested_at=COALESCE(deactivation_requested_at,now()) WHERE deletion_pending;
        CREATE TABLE IF NOT EXISTS deactivation_receipts(id uuid PRIMARY KEY,client_id uuid NOT NULL,instance_id uuid NOT NULL,token_hash text NOT NULL,confirmed_at timestamptz NOT NULL DEFAULT now());
        CREATE TABLE IF NOT EXISTS agent_updates (
          id uuid PRIMARY KEY, client_id uuid NOT NULL REFERENCES clients(id) ON DELETE CASCADE,
          target_version text NOT NULL, sha256 text NOT NULL CHECK(sha256 ~ '^[a-f0-9]{64}$'),
          state text NOT NULL DEFAULT 'Pending' CHECK(state IN ('Pending','Waiting','Downloading','Applying','Verifying','Succeeded','Failed','RolledBack','Cancelled')),
          requested_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),message text NOT NULL DEFAULT '');
        CREATE UNIQUE INDEX IF NOT EXISTS one_active_update ON agent_updates(client_id) WHERE state IN ('Pending','Waiting','Downloading','Applying','Verifying');
        """);
}

